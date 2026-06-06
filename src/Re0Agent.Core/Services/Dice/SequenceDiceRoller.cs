namespace Re0Agent.Core.Services.Dice;

public sealed class SequenceDiceRoller(IEnumerable<int> rolls) : IDiceRoller
{
    private readonly Queue<int> queue = new(rolls);

    public int RollD100()
    {
        if (queue.Count == 0)
        {
            throw new InvalidOperationException("No dice rolls remain in the sequence.");
        }

        var roll = queue.Dequeue();
        if (roll is < 1 or > 100)
        {
            throw new InvalidOperationException("D100 rolls must be between 1 and 100.");
        }

        return roll;
    }
}
