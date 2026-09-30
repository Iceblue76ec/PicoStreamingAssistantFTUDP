using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

public static class Pxr
{
    public const int BLEND_SHAPE_NUMS = 72;
}

public enum BlendShapeIndex
{
    EyeLookDown_L = 0,
    NoseSneer_L = 1,
    EyeLookIn_L = 2,
    BrowInnerUp = 3,
    BrowDown_R = 4,
    MouthClose = 5,
    MouthLowerDown_R = 6,
    JawOpen = 7,
    MouthUpperUp_R = 8,
    MouthShrugUpper = 9,
    MouthFunnel = 10,
    EyeLookIn_R = 11,
    EyeLookDown_R = 12,
    NoseSneer_R = 13,
    MouthRollUpper = 14,
    JawRight = 15,
    BrowDown_L = 16,
    MouthShrugLower = 17,
    MouthRollLower = 18,
    MouthSmile_L = 19,
    MouthPress_L = 20,
    MouthSmile_R = 21,
    MouthPress_R = 22,
    MouthDimple_R = 23,
    MouthLeft = 24,
    JawForward = 25,
    EyeSquint_L = 26,
    MouthFrown_L = 27,
    EyeBlink_L = 28,
    CheekSquint_L = 29,
    BrowOuterUp_L = 30,
    EyeLookUp_L = 31,
    JawLeft = 32,
    MouthStretch_L = 33,
    MouthPucker = 34,
    EyeLookUp_R = 35,
    BrowOuterUp_R = 36,
    CheekSquint_R = 37,
    EyeBlink_R = 38,
    MouthUpperUp_L = 39,
    MouthFrown_R = 40,
    EyeSquint_R = 41,
    MouthStretch_R = 42,
    CheekPuff = 43,
    EyeLookOut_L = 44,
    EyeLookOut_R = 45,
    EyeWide_R = 46,
    EyeWide_L = 47,
    MouthRight = 48,
    MouthDimple_L = 49,
    MouthLowerDown_L = 50,
    TongueOut = 51,
    PP = 52,
    CH = 53,
    o = 54,
    O = 55,
    I = 56,
    u = 57,
    RR = 58,
    XX = 59,
    aa = 60,
    i = 61,
    FF = 62,
    U = 63,
    TH = 64,
    kk = 65,
    SS = 66,
    e = 67,
    DD = 68,
    E = 69,
    nn = 70,
    sil = 71
};

// Legacy datagram layout: 16-byte header followed by PxrFTInfo.
// Offsets: start codes 0..1, tracking_type 2, sub_type 3, multi_packet 4,
// current_packet_index 5, version 6..7, header timestamp 8..15.
// The receiver checks only tracking_type == 2. It does not validate the start
// codes/version or read either timestamp. See docs/connector.md for boundaries.
// multi_packet/current_packet_index are not interpreted: there is no fragment
// assembly. All 72 weights must fit in one datagram, even if these fields are set.
// Configuration: https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TrackingDataHeader
{
    public byte start_code1;
    public byte start_code2;
    public byte tracking_type;
    public byte sub_type;
    public byte multi_packet;
    public byte current_packet_index;
    public ushort version;
    public ulong timestamp;
};

// Full payload: 892 bytes = timestamp 8 + weights 288 + trailing fields 596.
// Datagram offsets: timestamp 16..23 (skipped), weights 24..311 (consumed).
// The remaining 596 bytes are videoInputValid 40, laughingProb 4,
// emotionProb 40 and reserved 512. Tracking does not read any of them.
// Accepting the prefix alone is a receiver choice, not a claim that senders
// define those trailing fields as optional in the wire protocol.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PxrFTInfo
{
    public long timestamp;
    public BlendShapeWeight blendShapeWeight;
    public FloatBuffer videoInputValid;
    public float laughingProb;
    public FloatBuffer emotionProb;
    public ReservedFloatBuffer reserved;
};

[InlineArray(Pxr.BLEND_SHAPE_NUMS)]
public struct BlendShapeWeight
{
    private float _element0;
}

[InlineArray(10)]
public struct FloatBuffer
{
    private float _element0;
}

[InlineArray(128)]
public struct ReservedFloatBuffer
{
    private float _element0;
}