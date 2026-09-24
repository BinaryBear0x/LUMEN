using HuePC.Core.Models;

namespace HuePC.Core.Interfaces;

public interface ICapabilityDetector
{
    CapabilityReport Detect(GattDiscoverySnapshot discovery);
}
