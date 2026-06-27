using Microsoft.ML.OnnxRuntime;
using Steaming.Application.Models;

namespace Steaming.Application.Services;

public interface IFaceTrackingProvider : IDisposable
{
    string ProviderName { get; }
    void Load(string assetRoot, SessionOptions options);
    RawFaceTrackingFrame ProcessFrame(CameraFrame frame);
}
