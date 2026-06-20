namespace Re0Agent.Core.Services.Dice;

/// <summary>
/// 确定性骰子，按预设序列出点，供测试使用。
/// d100 与 d6 各自独立队列：默认单参构造喂 d100（兼容旧用例）；
/// 需要 2d6 时用双参构造分别提供 d6 序列。
/// </summary>
public sealed class SequenceDiceRoller : IDiceRoller
{
    private readonly Queue<int> d100Queue;
    private readonly Queue<int> d6Queue;

    public SequenceDiceRoller(IEnumerable<int> rolls)
        : this(rolls, [])
    {
    }

    public SequenceDiceRoller(IEnumerable<int> d100Rolls, IEnumerable<int> d6Rolls)
    {
        d100Queue = new Queue<int>(d100Rolls);
        d6Queue = new Queue<int>(d6Rolls);
    }

    /// <summary>仅提供 d6 序列的便捷构造。</summary>
    public static SequenceDiceRoller ForD6(params int[] d6Rolls)
    {
        return new SequenceDiceRoller([], d6Rolls);
    }

    public int RollD100()
    {
        if (d100Queue.Count == 0)
        {
            throw new InvalidOperationException("No d100 rolls remain in the sequence.");
        }

        var roll = d100Queue.Dequeue();
        if (roll is < 1 or > 100)
        {
            throw new InvalidOperationException("D100 rolls must be between 1 and 100.");
        }

        return roll;
    }

    public int RollD6()
    {
        if (d6Queue.Count == 0)
        {
            throw new InvalidOperationException("No d6 rolls remain in the sequence.");
        }

        var roll = d6Queue.Dequeue();
        if (roll is < 1 or > 6)
        {
            throw new InvalidOperationException("D6 rolls must be between 1 and 6.");
        }

        return roll;
    }
}
