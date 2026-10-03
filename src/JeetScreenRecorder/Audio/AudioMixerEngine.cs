targetispossible-max
JeetScreenRecorder
Repository navigation
Code
Issues
Pull requests
Actions
Projects
Wiki
Security and quality
Insights
Settings
Build JeetScreenRecorder
Update AudioMixerEngine.cs #10
All jobs
Run details
Triggered via push 2 minutes ago
@targetispossible-maxtargetispossible-max
pushed
 e786aa0
main
Status
Failure
Total duration
41s
Artifacts
–


Annotations
3 errors and 1 warning
build
Process completed with exit code 1.
build: src/JeetScreenRecorder/Audio/AudioMixerEngine.cs#L9
'AudioMixerEngine' does not implement interface member 'IAudioCaptureService.ReadPeaks()'. 'AudioMixerEngine.ReadPeaks()' cannot implement 'IAudioCaptureService.ReadPeaks()' because it does not have the matching return type of '(double Mic, double System)'.
build: src/JeetScreenRecorder/Audio/AudioMixerEngine.cs#L9
'AudioMixerEngine' does not implement interface member 'IAudioCaptureService.ReadPeaks()'. 'AudioMixerEngine.ReadPeaks()' cannot implement 'IAudioCaptureService.ReadPeaks()' because it does not have the matching return type of '(double Mic, double System)'.
build
Node.js 20 is deprecated. The following actions target Node.js 20 but are being forced to run on Node.js 24: actions/checkout@v4, actions/setup-dotnet@v4. For more information see: https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners/
