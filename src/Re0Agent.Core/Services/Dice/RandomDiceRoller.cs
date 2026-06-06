namespace Re0Agent.Core.Services.Dice;

public sealed class RandomDiceRoller : IDiceRoller
{
    private readonly Random random = new();

    public int RollD100()
    {
        return random.Next(1, 101);
    }
}
