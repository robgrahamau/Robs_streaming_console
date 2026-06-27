using System.Text.Json;
using Steaming.Application.Models;

namespace Steaming.Application.Services;

public sealed class FaceTrackingPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string RootDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steaming");

    private static readonly string SettingsPath = Path.Combine(RootDir, "face_tracking.json");
    private static readonly string CalibrationPath = Path.Combine(RootDir, "face_tracking_calibration.json");

    public FaceTrackingSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new FaceTrackingSettings();

            return JsonSerializer.Deserialize<FaceTrackingSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new FaceTrackingSettings();
        }
        catch
        {
            return new FaceTrackingSettings();
        }
    }

    public FaceTrackingCalibrationProfile LoadCalibration()
    {
        try
        {
            if (!File.Exists(CalibrationPath))
                return new FaceTrackingCalibrationProfile();

            return JsonSerializer.Deserialize<FaceTrackingCalibrationProfile>(File.ReadAllText(CalibrationPath), JsonOptions)
                ?? new FaceTrackingCalibrationProfile();
        }
        catch
        {
            return new FaceTrackingCalibrationProfile();
        }
    }

    public void SaveSettings(FaceTrackingSettings settings)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public void SaveCalibration(FaceTrackingCalibrationProfile calibration)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(CalibrationPath, JsonSerializer.Serialize(calibration, JsonOptions));
    }
}
