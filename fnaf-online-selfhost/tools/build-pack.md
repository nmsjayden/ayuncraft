# Rebuilding the pack (for a new game version)

You only need this if the game updates. The pack the players get is: BepInEx 6 IL2CPP + `FnafSelfHost.dll` + `Open.Nat.dll`
+ the Unity base-libs zip for the game's Unity version.

1. Get BepInEx (bleeding edge, IL2CPP, win-x64) from https://builds.bepinex.dev/projects/bepinex_be and unzip it.
2. Get the Unity base libs: `https://unity.bepinex.dev/libraries/<unity version>.zip` (the version is in `UnityPlayer.dll`, e.g. 6000.0.24).
3. Generate the interop assemblies (what BepInEx does on first launch) so you can compile against them without running the game:

       dotnet build tools/InteropGen -c Release -p:BepInExCore=<unzipped BepInEx>/BepInEx/core
       dotnet tools/InteropGen/bin/Release/net8.0/InteropGen.dll <game>/GameAssembly.dll \
              "<game>/FNAF Online Multiplayer_Data/il2cpp_data/Metadata/global-metadata.dat" ./interop ./unity-libs-unzipped

   (On a Windows PC with the game and BepInEx installed you can skip this: BepInEx already made `BepInEx/interop`.)
4. Compile the plugin:  `dotnet build SelfHostPlugin -c Release -p:InteropDir=<path to interop>`
5. Zip: BepInEx files + `BepInEx/plugins/FnafSelfHost/{FnafSelfHost.dll,Open.Nat.dll}` + `BepInEx/unity-libs/<version>.zip`.

If method names changed in the new version, `Plugin.cs` will fail to patch; the names it targets are listed at the top of each patch.
