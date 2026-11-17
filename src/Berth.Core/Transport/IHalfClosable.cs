namespace Berth.Transport;

/// <summary>
/// A transport stream whose sending side can be closed alone, so the far end reads the end of the input while its output can still be
/// read (an SSH channel's EOF). TCP and TLS streams are told apart by their own types; this is for the others.
/// </summary>
public interface IHalfClosable
{
    /// <summary>Closes the sending side. Writing afterwards throws.</summary>
    Task CloseWriteAsync();
}
