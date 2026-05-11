using ProtoBuf;

namespace CaveinFix;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ConfigSyncPacket
{
    public float InstabilityMultiplier { get; set; } = 1.0f;
}