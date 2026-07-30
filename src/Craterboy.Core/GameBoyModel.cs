namespace Craterboy;

public enum GameBoyModel
{
    DmgB,
    Mgb,
    Cgb0,
    CgbA,
    CgbB,
    CgbC,
    CgbD,
    CgbE,
    AgbA,
    GbpA,
    Sgb,
    Sgb2,
}

public static class GameBoyModelExtensions
{
    public static bool IsColor(this GameBoyModel model) =>
        model is >= GameBoyModel.Cgb0 and <= GameBoyModel.GbpA;

    public static bool IsSuperGameBoy(this GameBoyModel model) =>
        model is GameBoyModel.Sgb or GameBoyModel.Sgb2;
}
