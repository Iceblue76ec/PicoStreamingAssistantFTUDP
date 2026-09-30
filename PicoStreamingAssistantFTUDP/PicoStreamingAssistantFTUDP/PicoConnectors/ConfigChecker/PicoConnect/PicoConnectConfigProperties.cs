using System.Text.Json.Serialization;

namespace Pico4SAFTExtTrackingModule.PicoConnectors.ConfigChecker.PicoConnect;

public sealed class Config
{
    [JsonPropertyName("lab")]
    public Lab? Lab { get; set; }
}

// Only faceTrackingTransferProtocol is read. Unmodelled settings, including
// faceTrackingMode, are ignored by System.Text.Json. Hybrid mode can zero the
// sender's mouth blendshapes during speech; the module does not combine visemes
// with mouth weights. The upstream hybrid test remains ignored.
// Configuration: https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe
public sealed class Lab
{
    [JsonPropertyName("faceTrackingTransferProtocol")]
    public int FaceTrackingTransferProtocol { get; set; }
}