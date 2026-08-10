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
    public static bool IsCgbRevision(this GameBoyModel model) =>
        model is >= GameBoyModel.Cgb0 and <= GameBoyModel.CgbE;

    public static bool IsAgb(this GameBoyModel model) => model is GameBoyModel.AgbA;

    public static bool IsGbp(this GameBoyModel model) => model is GameBoyModel.GbpA;

    public static bool IsColor(this GameBoyModel model) =>
        model.IsCgbRevision() || model.IsAgb() || model.IsGbp();

    public static bool IsSuperGameBoy(this GameBoyModel model) =>
        model is GameBoyModel.Sgb or GameBoyModel.Sgb2;
}
