using System;

namespace Tesserafin.Controller.Session;

/// <summary>
/// A device's access token was revoked: the device was deleted, the user signed out of it, or the
/// user's other tokens were revoked. Everything the device holds open on the server must end.
/// </summary>
public sealed class DeviceLoggedOutEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceLoggedOutEventArgs"/> class.
    /// </summary>
    /// <param name="deviceId">The device whose token was revoked.</param>
    /// <param name="userId">The user the revoked token belonged to.</param>
    public DeviceLoggedOutEventArgs(string deviceId, Guid userId)
    {
        DeviceId = deviceId;
        UserId = userId;
    }

    /// <summary>
    /// Gets the device whose token was revoked.
    /// </summary>
    public string DeviceId { get; }

    /// <summary>
    /// Gets the user the revoked token belonged to.
    /// </summary>
    public Guid UserId { get; }
}
