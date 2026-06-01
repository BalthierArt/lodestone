namespace Lodestone.Models;

public sealed class SubmarineReturn
{
    public string Id { get; set; } = string.Empty;
    public ulong CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public DateTime ReturnAt { get; set; }
    public uint ReturnUnixSeconds { get; set; }
    public bool WorkshopEnabled { get; set; }
    public bool CharacterExcluded { get; set; }
    public bool EnabledInAutoRetainer { get; set; }
    public bool WaitForAllDeployables { get; set; }
    public int Level { get; set; }
    public uint CurrentExp { get; set; }
    public uint NextLevelExp { get; set; }
    public string Behavior { get; set; } = string.Empty;
    public string SelectedPointPlan { get; set; } = string.Empty;
    public string SelectedUnlockPlan { get; set; } = string.Empty;
    public byte[] Points { get; set; } = [];

    public bool Returned => ReturnAt <= DateTime.Now;
    public DateTime Date => ReturnAt.Date;
}
