namespace Re0Agent.Core.Services.Dice;

public interface IDiceRoller
{
    int RollD100();

    /// <summary>掷一颗 6 面骰，返回 1-6。</summary>
    int RollD6();
}
