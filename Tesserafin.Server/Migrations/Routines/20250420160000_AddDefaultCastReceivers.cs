namespace Tesserafin.Server.Migrations.Routines;

/// <summary>
/// Retired: this routine added the default Google Cast receivers to the system configuration. The
/// cast receivers went with the Chromecast player (tesserafin#129 step 7), so it does nothing; it
/// stays registered so a server that already applied it keeps the same migration history.
/// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
[TesserafinMigration("2025-04-20T16:00:00", nameof(AddDefaultCastReceivers), "34A1A1C4-5572-418E-A2F8-32CDFE2668E8", RunMigrationOnSetup = true)]
public class AddDefaultCastReceivers : IMigrationRoutine
#pragma warning restore CS0618 // Type or member is obsolete
{
    /// <inheritdoc />
    public void Perform()
    {
    }
}
