using LanPong;
using LanPong.TrainingData;

// Apply the same native pre-ORT startup guard for offline and production evaluation.
OnnxRuntimeStartup.DisablePosixTelemetry();

return TrainingCli.Run(args);
