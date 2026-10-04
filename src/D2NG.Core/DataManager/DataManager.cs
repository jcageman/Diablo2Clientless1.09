namespace D2NG.Core.DataManager;

/// <summary>
/// The game tables item parsing needs. They are looked up through <see cref="GameDataLocation"/>, so
/// the host decides which folder they come from.
/// </summary>
internal sealed class DataManager
{
    private static DataManager sm_instance;

    public static DataManager Instance
    {
        get
        {
            sm_instance ??= new DataManager();
            return sm_instance;
        }
    }

    /// <summary>Drops the loaded tables so the next use picks up a newly configured folder.</summary>
    internal static void Reset() => sm_instance = null;

    public ItemDataType ItemData { get; }

    public ItemPropertyDataType ItemPropertyData { get; }

    private DataManager()
    {
        ItemData = new ItemDataType(GameDataLocation.Resolve(GameDataLocation.ItemDataFile));
        ItemPropertyData = new ItemPropertyDataType(GameDataLocation.Resolve(GameDataLocation.ItemPropertiesFile));
    }
}
