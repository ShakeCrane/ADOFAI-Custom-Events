# Unity depth probe

Minimal Built-in pipeline shaders for the M1 feasibility test. The red shader
uses an unnamed GrabPass; the green shader has no scene sampling. These are
diagnostic fixtures, not the Phira effect and not a production bundle.

Use the Unity 6000.3.25f1 editor with Windows build support and a valid license.
Set `PEV_PROBE_BUNDLE_OUTPUT` to an absolute output directory and run:

```
Unity.exe -batchmode -quit -projectPath <this-directory> -executeMethod BuildProbeBundle.Build -logFile <log-path>
```

Building is only the first step. Load the resulting bundle in the actual game,
then verify foreground protection, two independently sampled depths, zero
copies in the no-grab path, and measured performance. A successful build does
not establish any of those properties.
