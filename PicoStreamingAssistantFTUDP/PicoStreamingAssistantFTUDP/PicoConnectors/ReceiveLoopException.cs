namespace Pico4SAFTExtTrackingModule.PicoConnectors;

// The receiver has stopped, even when the original exception is not a socket error.
internal sealed class ReceiveLoopException(Exception inner)
    : Exception("The PICO UDP receive loop stopped unexpectedly.", inner);
