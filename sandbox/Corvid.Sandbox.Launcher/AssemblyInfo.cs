using System.Runtime.Versioning;

// bubblewrap, Landlock and seccomp are Linux facilities. Container and SSH backends come later (corvid-agent.md §10.1).
[assembly: SupportedOSPlatform("linux")]
