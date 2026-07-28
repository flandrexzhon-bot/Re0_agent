using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Database;

public sealed class ChatSessionService(
    Re0AgentDbContext dbContext,
    ProtagonistTemplateService templateService,
    ProjectionReplayer projectionReplayer,
    EventSingleWriter eventWriter)
{
    public async Task EnsureDefaultSessionAsync(CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);
        if (await dbContext.ChatSessions.AnyAsync(cancellationToken))
        {
            return;
        }

        await templateService.EnsureDefaultTemplateAsync(cancellationToken);
        var template = await dbContext.ProtagonistTemplates
            .FirstOrDefaultAsync(item => item.IsDefault == 1, cancellationToken);
        var session = await CreateSessionAsync("默认会话", "RP", activate: true, cancellationToken);
        if (template is not null)
        {
            await InitializeSessionAsync(session, template.TemplateId, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ChatSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultSessionAsync(cancellationToken);
        return await dbContext.ChatSessions.OrderByDescending(item => item.SessionId).ToListAsync(cancellationToken);
    }

    public async Task<ChatSession?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultSessionAsync(cancellationToken);
        return await dbContext.ChatSessions.FirstOrDefaultAsync(item => item.IsActive == 1, cancellationToken);
    }

    public async Task<ChatSession> CreateSessionAsync(
        string sessionName,
        string gameMode,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        if (gameMode is not ("RP" or "Theater"))
        {
            throw new ArgumentOutOfRangeException(nameof(gameMode), "游戏模式必须为 RP 或 Theater。");
        }

        var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            if (activate)
            {
                await dbContext.ChatSessions.ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.IsActive, 0), cancellationToken);
            }

            var session = new ChatSession
            {
                SessionName = string.IsNullOrWhiteSpace(sessionName) ? "未命名会话" : sessionName.Trim(),
                IsActive = activate ? 1 : 0,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                GameMode = gameMode,
                WorldClockAnchor = DateTimeOffset.UtcNow.ToString("O")
            };
            dbContext.ChatSessions.Add(session);
            await dbContext.SaveChangesAsync(cancellationToken);

            var branch = new TimelineBranch
            {
                BranchId = Guid.NewGuid().ToString("N"),
                SessionId = session.SessionId,
                BranchReason = "session_start",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            };
            dbContext.TimelineBranches.Add(branch);
            session.CurrentBranchId = branch.BranchId;
            await dbContext.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return session;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
    }

    public async Task<ChatSession> CreateNewSessionAsync(
        string sessionName,
        string gameMode,
        CancellationToken cancellationToken = default)
    {
        await templateService.EnsureDefaultTemplateAsync(cancellationToken);
        var template = await dbContext.ProtagonistTemplates
            .FirstOrDefaultAsync(item => item.IsDefault == 1, cancellationToken);
        var session = await CreateSessionAsync(sessionName, gameMode, activate: true, cancellationToken);
        if (template is not null)
        {
            await InitializeSessionAsync(session, template.TemplateId, cancellationToken);
        }
        return session;
    }

    public async Task SwitchSessionAsync(int targetSessionId, CancellationToken cancellationToken = default)
    {
        var target = await dbContext.ChatSessions.SingleOrDefaultAsync(item => item.SessionId == targetSessionId, cancellationToken);
        if (target is null || string.IsNullOrWhiteSpace(target.CurrentBranchId))
        {
            throw new InvalidOperationException("找不到目标会话。");
        }

        await dbContext.ChatSessions.ExecuteUpdateAsync(
            setters => setters.SetProperty(item => item.IsActive, item => item.SessionId == targetSessionId ? 1 : 0),
            cancellationToken);
        var cursor = await dbContext.TimelineEvents.Where(item => item.BranchId == target.CurrentBranchId && item.Status == "Committed")
            .OrderByDescending(item => item.Sequence).Select(item => item.EventId).FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            await projectionReplayer.ReplayToEventAsync(target.CurrentBranchId, cursor, cancellationToken);
        }
    }

    public async Task<int> BranchSessionAsync(
        int sourceSessionId,
        string parentEventId,
        string newName,
        string branchReason,
        CancellationToken cancellationToken = default)
    {
        var source = await dbContext.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.SessionId == sourceSessionId, cancellationToken)
            ?? throw new InvalidOperationException("找不到分支来源会话。");
        if (string.IsNullOrWhiteSpace(source.CurrentBranchId))
        {
            throw new InvalidOperationException("来源会话没有有效时间线分支。");
        }

        var parentEventExists = await dbContext.TimelineEvents.AnyAsync(
            item => item.EventId == parentEventId && item.BranchId == source.CurrentBranchId && item.Status == "Committed",
            cancellationToken);
        if (!parentEventExists)
        {
            throw new InvalidOperationException("分支游标不属于来源会话的当前分支。");
        }
        var session = await CreateSessionAsync(newName, source.GameMode, activate: false, cancellationToken);
        var rootBranch = await dbContext.TimelineBranches
            .SingleAsync(item => item.BranchId == session.CurrentBranchId, cancellationToken);
        rootBranch.ParentBranchId = source.CurrentBranchId;
        rootBranch.ParentEventId = parentEventId;
        rootBranch.BranchReason = branchReason;
        await dbContext.SaveChangesAsync(cancellationToken);
        await SwitchSessionAsync(session.SessionId, cancellationToken);
        return session.SessionId;
    }

    public async Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.FindAsync([sessionId], cancellationToken);
        if (session is null)
        {
            return;
        }
        if (await dbContext.ChatSessions.CountAsync(cancellationToken) <= 1)
        {
            throw new InvalidOperationException("无法删除唯一的聊天记录。");
        }

        if (session.IsActive == 1)
        {
            var replacement = await dbContext.ChatSessions
                .Where(item => item.SessionId != sessionId)
                .OrderByDescending(item => item.SessionId)
                .FirstAsync(cancellationToken);
            replacement.IsActive = 1;
        }

        var branchIds = await dbContext.TimelineBranches.Where(item => item.SessionId == sessionId)
            .Select(item => item.BranchId).ToListAsync(cancellationToken);
        var eventIds = await dbContext.TimelineEvents.Where(item => branchIds.Contains(item.BranchId))
            .Select(item => item.EventId).ToListAsync(cancellationToken);
        var saveIds = await dbContext.SavePoints.Where(item => branchIds.Contains(item.BranchId))
            .Select(item => item.SaveId).ToListAsync(cancellationToken);
        await dbContext.DeathReturnLog.Where(item => item.SavePointId != null && saveIds.Contains(item.SavePointId.Value)).ExecuteDeleteAsync(cancellationToken);
        await dbContext.SavePoints.Where(item => branchIds.Contains(item.BranchId)).ExecuteDeleteAsync(cancellationToken);
        await dbContext.ProjectionCommandLogs.Where(item => eventIds.Contains(item.EventId)).ExecuteDeleteAsync(cancellationToken);
        await dbContext.TimelineEvents.Where(item => branchIds.Contains(item.BranchId)).ExecuteDeleteAsync(cancellationToken);
        await dbContext.ProjectionCheckpoints.Where(item => branchIds.Contains(item.BranchId)).ExecuteDeleteAsync(cancellationToken);
        await dbContext.TimelineBranches.Where(item => item.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
        dbContext.ChatSessions.Remove(session);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task InitializeSessionAsync(ChatSession session, int templateId, CancellationToken cancellationToken)
    {
        var stateChanges = await templateService.CreateInitialStateChangeSetAsync(templateId, cancellationToken: cancellationToken);
        await eventWriter.CommitAsync(
            session.SessionId,
            "InitialProjection",
            "{}",
            stateChangeSet: stateChanges,
            triggerCause: "template_initialization",
            cancellationToken: cancellationToken);
    }
}
