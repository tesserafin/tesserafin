using System;

namespace Tesserafin.Controller.Session;

/// <summary>
/// Raises <see cref="DeviceLoggedOut"/> when a device's access token is revoked (#413). Kept apart
/// from <see cref="ISessionManager"/> so that the published interface does not grow a member: the
/// session manager implements both, and a consumer that holds an <see cref="ISessionManager"/>
/// subscribes when that instance is also a source.
/// </summary>
public interface IDeviceLoggedOutSource
{
    /// <summary>
    /// Occurs, synchronously, when a device's access token is revoked
    /// (<see cref="ISessionManager.Logout(Tesserafin.Data.Entities.Security.Device)"/>):
    /// after its capabilities are revoked and before the call returns, so what the device holds
    /// open can be ended by the time the revocation is answered.
    /// </summary>
    event EventHandler<DeviceLoggedOutEventArgs> DeviceLoggedOut;
}
