using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Pico4SAFTExtTrackingModule.BlendshapeScaler;
using Pico4SAFTExtTrackingModule.PacketLogger;
using Pico4SAFTExtTrackingModule.PicoConnectors;
using Pico4SAFTExtTrackingModule.PicoConnectors.ConfigChecker;
using Pico4SAFTExtTrackingModule.PicoConnectors.ProgramChecker;
using System.Diagnostics;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace Pico4SAFTExtTrackingModule;

public sealed partial class Pico4SAFTExtTrackingModule : ExtTrackingModule, IDisposable
{
    private int _disposed;
    private IPicoConnector? _connector;
    private IBlendshapeScaler? _scaler;
    private PacketLogger<PxrFTInfo>? _logger;
    private readonly Func<IPicoConnector?> _connectorFactory;
    private readonly Func<DateTime> _utcNow;
    private DateTime _nextConnectionAttempt;
    private DateTime _nextUpdateAttempt;
    public static readonly string LoggerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "PICOLogs.csv");
    public (bool Eye, bool Expression) TrackingState = (false, false);
    public override (bool SupportsEye, bool SupportsExpression) Supported { get; } = (true, true);

    public Pico4SAFTExtTrackingModule() : this(null, null) { }
    public Pico4SAFTExtTrackingModule(IPicoConnector? connector, IBlendshapeScaler? scaler)
        : this(connector, scaler, null, null) { }
    internal Pico4SAFTExtTrackingModule(IPicoConnector? connector, IBlendshapeScaler? scaler,
        Func<IPicoConnector?>? connectorFactory, Func<DateTime>? utcNow)
    {
        _connector = connector;
        _scaler = scaler;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _connectorFactory = connectorFactory ?? (() => ConnectorFactory.Build(Logger,
            new ProcessRunningProgramChecker(), new ConfigChecker(Logger)));
    }

    public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
    {
        TrackingState = (eyeAvailable, expressionAvailable);
        if ((!eyeAvailable && !expressionAvailable) || Volatile.Read(ref _disposed) != 0) return (false, false);
        _scaler ??= new FileBlendshapeScalerFactory().Build(Logger);
#if FILE_LOG
        _logger = PicoDataLoggerFactory.Build(LoggerPath);
#endif
        ModuleInformation.Name = "Pico 4 Pro / Enterprise";
        if (typeof(Pico4SAFTExtTrackingModule).Assembly.GetManifestResourceStream("pico-hmd.png") is { } stream)
        {
            if (ModuleInformation.StaticImages == null) ModuleInformation.StaticImages = [stream];
            else ModuleInformation.StaticImages.Add(stream);
        }
        return TrackingState;
    }

    private bool TryConnect()
    {
        if (_utcNow() < _nextConnectionAttempt)
        {
            Thread.Sleep(100);
            return false;
        }
        try
        {
            _connector = _connectorFactory();
            if (_connector != null && _connector.Connect())
            {
                if (Volatile.Read(ref _disposed) != 0) { ResetConnector(); return false; }
                _nextConnectionAttempt = default;
                return true;
            }
        }
        catch (Exception exception) { LogConnectionFailed(exception); }
        ResetConnector();
        _nextConnectionAttempt = _utcNow().AddSeconds(5);
        Thread.Sleep(100);
        return false;
    }

    private void ResetConnector()
    {
        try { Interlocked.Exchange(ref _connector, null)?.Teardown(); }
        catch (Exception exception) { LogCleanupFailed(exception); }
    }

    private void BackOffUpdate(Exception exception, bool receiving)
    {
        bool rebuild = receiving && exception is ReceiveLoopException or SocketException or ObjectDisposedException;
        if (exception is ReceiveLoopException) LogReceiveRecovery();
        else LogUpdateFailed(exception, receiving ? "receive" : "processing", rebuild);
        if (rebuild) ResetConnector();
        _nextUpdateAttempt = _utcNow().AddSeconds(1);
    }

    private void UpdateEye(ReadOnlySpan<float> pxrShape, ref UnifiedSingleEyeData left, ref UnifiedSingleEyeData right)
    {
        Debug.Assert(_scaler is not null);

        // to be tested, not entirely sure how Pxr blink/squint will translate to Openness.
        left.Openness = _scaler.EyeExpressionShapeScale(1f - pxrShape[(int)BlendShapeIndex.EyeBlink_L], EyeExpressions.EyeOpennessLeft);
        right.Openness = _scaler.EyeExpressionShapeScale(1f - pxrShape[(int)BlendShapeIndex.EyeBlink_R], EyeExpressions.EyeOpennessRight);

        left.Gaze.x = _scaler.EyeExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeLookIn_L] - pxrShape[(int)BlendShapeIndex.EyeLookOut_L], EyeExpressions.EyeXGazeLeft);
        left.Gaze.y = _scaler.EyeExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeLookUp_L] - pxrShape[(int)BlendShapeIndex.EyeLookDown_L], EyeExpressions.EyeYGazeLeft);

        right.Gaze.x = _scaler.EyeExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeLookOut_R] - pxrShape[(int)BlendShapeIndex.EyeLookIn_R], EyeExpressions.EyeXGazeRight);
        right.Gaze.y = _scaler.EyeExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeLookUp_R] - pxrShape[(int)BlendShapeIndex.EyeLookDown_R], EyeExpressions.EyeXGazeLeft);
    }

    private void UpdateEyeExpression(ReadOnlySpan<float> pxrShape, Span<UnifiedExpressionShape> unifiedShape)
    {
        Debug.Assert(_scaler is not null);

        #region Brow Shapes
        unifiedShape[(int)UnifiedExpressions.BrowInnerUpLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowInnerUp], UnifiedExpressions.BrowInnerUpLeft);
        unifiedShape[(int)UnifiedExpressions.BrowInnerUpRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowInnerUp], UnifiedExpressions.BrowInnerUpRight);
        unifiedShape[(int)UnifiedExpressions.BrowOuterUpLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowOuterUp_L], UnifiedExpressions.BrowOuterUpLeft);
        unifiedShape[(int)UnifiedExpressions.BrowOuterUpRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowOuterUp_R], UnifiedExpressions.BrowOuterUpRight);
        unifiedShape[(int)UnifiedExpressions.BrowLowererLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowDown_L], UnifiedExpressions.BrowLowererLeft);
        unifiedShape[(int)UnifiedExpressions.BrowPinchLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowDown_L], UnifiedExpressions.BrowPinchLeft);
        unifiedShape[(int)UnifiedExpressions.BrowLowererRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowDown_R], UnifiedExpressions.BrowLowererRight);
        unifiedShape[(int)UnifiedExpressions.BrowPinchRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.BrowDown_R], UnifiedExpressions.BrowPinchRight);
        #endregion
        #region Eye Shapes
        unifiedShape[(int)UnifiedExpressions.EyeSquintLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeSquint_L], UnifiedExpressions.EyeSquintLeft);
        unifiedShape[(int)UnifiedExpressions.EyeSquintRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeSquint_R], UnifiedExpressions.EyeSquintRight);
        unifiedShape[(int)UnifiedExpressions.EyeWideLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeWide_L], UnifiedExpressions.EyeWideLeft);
        unifiedShape[(int)UnifiedExpressions.EyeWideRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.EyeWide_R], UnifiedExpressions.EyeWideRight);
        #endregion
    }

    private void UpdateExpression(ReadOnlySpan<float> pxrShape, Span<UnifiedExpressionShape> unifiedShape)
    {
        // TODO: Map Viseme shapes onto face shapes.
        Debug.Assert(_scaler is not null);

        #region Jaw
        unifiedShape[(int)UnifiedExpressions.JawOpen].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.JawOpen], UnifiedExpressions.JawOpen);
        unifiedShape[(int)UnifiedExpressions.JawLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.JawLeft], UnifiedExpressions.JawLeft);
        unifiedShape[(int)UnifiedExpressions.JawRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.JawRight], UnifiedExpressions.JawRight);
        unifiedShape[(int)UnifiedExpressions.JawForward].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.JawForward], UnifiedExpressions.JawForward);
        unifiedShape[(int)UnifiedExpressions.MouthClosed].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthClose], UnifiedExpressions.MouthClosed);
        #endregion
        #region Cheek
        unifiedShape[(int)UnifiedExpressions.CheekPuffLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.CheekPuff], UnifiedExpressions.CheekPuffLeft);
        unifiedShape[(int)UnifiedExpressions.CheekPuffRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.CheekPuff], UnifiedExpressions.CheekPuffRight);
        unifiedShape[(int)UnifiedExpressions.CheekSquintLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.CheekSquint_L], UnifiedExpressions.CheekSquintLeft);
        unifiedShape[(int)UnifiedExpressions.CheekSquintRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.CheekSquint_R], UnifiedExpressions.CheekSquintRight);
        #endregion
        #region Nose
        unifiedShape[(int)UnifiedExpressions.NoseSneerLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.NoseSneer_L], UnifiedExpressions.NoseSneerLeft);
        unifiedShape[(int)UnifiedExpressions.NoseSneerRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.NoseSneer_R], UnifiedExpressions.NoseSneerRight);
        #endregion
        #region Mouth
        unifiedShape[(int)UnifiedExpressions.MouthUpperUpLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthUpperUp_L], UnifiedExpressions.MouthUpperUpLeft);
        unifiedShape[(int)UnifiedExpressions.MouthUpperUpRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthUpperUp_R], UnifiedExpressions.MouthUpperUpRight);
        unifiedShape[(int)UnifiedExpressions.MouthLowerDownLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthLowerDown_L], UnifiedExpressions.MouthLowerDownLeft);
        unifiedShape[(int)UnifiedExpressions.MouthLowerDownRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthLowerDown_R], UnifiedExpressions.MouthLowerDownRight);
        unifiedShape[(int)UnifiedExpressions.MouthFrownLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthFrown_L], UnifiedExpressions.MouthFrownLeft);
        unifiedShape[(int)UnifiedExpressions.MouthFrownRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthFrown_R], UnifiedExpressions.MouthFrownRight);
        unifiedShape[(int)UnifiedExpressions.MouthDimpleLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthDimple_L], UnifiedExpressions.MouthDimpleLeft);
        unifiedShape[(int)UnifiedExpressions.MouthDimpleRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthDimple_R], UnifiedExpressions.MouthDimpleRight);
        unifiedShape[(int)UnifiedExpressions.MouthUpperLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthLeft], UnifiedExpressions.MouthUpperLeft);
        unifiedShape[(int)UnifiedExpressions.MouthLowerLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthLeft], UnifiedExpressions.MouthLowerLeft);
        unifiedShape[(int)UnifiedExpressions.MouthUpperRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthRight], UnifiedExpressions.MouthUpperRight);
        unifiedShape[(int)UnifiedExpressions.MouthLowerRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthRight], UnifiedExpressions.MouthLowerRight);
        unifiedShape[(int)UnifiedExpressions.MouthPressLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthPress_L], UnifiedExpressions.MouthPressLeft);
        unifiedShape[(int)UnifiedExpressions.MouthPressRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthPress_R], UnifiedExpressions.MouthPressRight);
        unifiedShape[(int)UnifiedExpressions.MouthRaiserLower].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthShrugLower], UnifiedExpressions.MouthRaiserLower);
        unifiedShape[(int)UnifiedExpressions.MouthRaiserUpper].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthShrugUpper], UnifiedExpressions.MouthRaiserUpper);
        unifiedShape[(int)UnifiedExpressions.MouthCornerPullLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthSmile_L], UnifiedExpressions.MouthCornerPullLeft);
        unifiedShape[(int)UnifiedExpressions.MouthCornerSlantLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthSmile_L], UnifiedExpressions.MouthCornerSlantLeft);
        unifiedShape[(int)UnifiedExpressions.MouthCornerPullRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthSmile_R], UnifiedExpressions.MouthCornerPullRight);
        unifiedShape[(int)UnifiedExpressions.MouthCornerSlantRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthSmile_R], UnifiedExpressions.MouthCornerSlantRight);
        unifiedShape[(int)UnifiedExpressions.MouthStretchLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthStretch_L], UnifiedExpressions.MouthStretchLeft);
        unifiedShape[(int)UnifiedExpressions.MouthStretchRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthStretch_R], UnifiedExpressions.MouthStretchRight);
        #endregion
        #region Lip
        unifiedShape[(int)UnifiedExpressions.LipFunnelUpperLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthFunnel], UnifiedExpressions.LipFunnelUpperLeft);
        unifiedShape[(int)UnifiedExpressions.LipFunnelUpperRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthFunnel], UnifiedExpressions.LipFunnelUpperRight);
        unifiedShape[(int)UnifiedExpressions.LipFunnelLowerLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthFunnel], UnifiedExpressions.LipFunnelLowerLeft);
        unifiedShape[(int)UnifiedExpressions.LipFunnelLowerRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthFunnel], UnifiedExpressions.LipFunnelLowerRight);
        unifiedShape[(int)UnifiedExpressions.LipPuckerUpperLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthPucker], UnifiedExpressions.LipPuckerUpperLeft);
        unifiedShape[(int)UnifiedExpressions.LipPuckerUpperRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthPucker], UnifiedExpressions.LipPuckerUpperRight);
        unifiedShape[(int)UnifiedExpressions.LipPuckerLowerLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthPucker], UnifiedExpressions.LipPuckerLowerLeft);
        unifiedShape[(int)UnifiedExpressions.LipPuckerLowerRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthPucker], UnifiedExpressions.LipPuckerLowerRight);
        unifiedShape[(int)UnifiedExpressions.LipSuckUpperLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthRollUpper], UnifiedExpressions.LipSuckUpperLeft);
        unifiedShape[(int)UnifiedExpressions.LipSuckUpperRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthRollUpper], UnifiedExpressions.LipSuckUpperRight);
        unifiedShape[(int)UnifiedExpressions.LipSuckLowerLeft].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthRollLower], UnifiedExpressions.LipSuckLowerLeft);
        unifiedShape[(int)UnifiedExpressions.LipSuckLowerRight].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.MouthRollLower], UnifiedExpressions.LipSuckLowerRight);
        #endregion
        #region Tongue
        unifiedShape[(int)UnifiedExpressions.TongueOut].Weight = _scaler.UnifiedExpressionShapeScale(pxrShape[(int)BlendShapeIndex.TongueOut], UnifiedExpressions.TongueOut);
        #endregion
    }

    public override void Update()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Status != ModuleState.Active || _utcNow() < _nextUpdateAttempt)
        {
            Thread.Sleep(100);
            return;
        }
        if (_connector == null && !TryConnect()) return;
        var connector = _connector;
        if (connector == null) return;
        ReadOnlySpan<float> shapes;
        try { shapes = connector.GetBlendShapes(); }
        catch (Exception exception) { BackOffUpdate(exception, receiving: true); return; }
        if (shapes.IsEmpty) return;
        try
        {
            if (_logger != null)
            {
                var data = PicoDataLoggerHelper.FillPxrFTInfo(shapes);
                _logger.UpdateValue(data);
            }
            Span<UnifiedExpressionShape> unified = UnifiedTracking.Data.Shapes;
            if (TrackingState.Eye)
            {
                UpdateEye(shapes, ref UnifiedTracking.Data.Eye.Left, ref UnifiedTracking.Data.Eye.Right);
                UpdateEyeExpression(shapes, unified);
            }
            if (TrackingState.Expression) UpdateExpression(shapes, unified);
        }
        catch (Exception exception) { BackOffUpdate(exception, receiving: false); }
    }

    public override void Teardown() => Dispose();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ResetConnector();
        _logger?.Dispose();
        _logger = null;
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(LogLevel.Warning, "Service discovery or connection failed; retry in 5000ms.")]
    private partial void LogConnectionFailed(Exception exception);
    [LoggerMessage(LogLevel.Warning, "Could not close the connector; recovery will continue.")]
    private partial void LogCleanupFailed(Exception exception);
    [LoggerMessage(LogLevel.Warning, "stage={stage}; rebuild={rebuild}; update backoff=1000ms.")]
    private partial void LogUpdateFailed(Exception exception, string stage, bool rebuild);
    [LoggerMessage(LogLevel.Warning, "Receive loop stopped; rebuilding local listener after 1000ms.")]
    private partial void LogReceiveRecovery();
}
