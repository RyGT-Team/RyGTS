adcpatch - rewrites GT Sport (v1.69) AdHoc scripts for RyGTS's offline features

Patched scripts go in <game folder>\patches\scripts\ (same file name as the game's); the renderer
(GTS-005) serves them in place of the originals. Do not distribute the patched .ADC files: they are
built from Polyphony's scripts. Inputs come from the game's volume (GTToolsSharp unpack).

  all <game folder> <update folder or ""> <scripts folder>
      What RyGTS.exe runs at Set up (and at Play when a script is missing): reads the five scripts
      from the player's own gt.idx volumes (update first, then base), patches each one and writes
      them to <scripts folder>. The .ad files must sit next to adcpatch.exe. ADCPATCH_KEEP=1 keeps
      the extracted originals. Releases ship it published self-contained in toolsdcpatch\.

  uikit      PROJECTS\GT7SP\UI_KIT\UI_KIT.ADC      openGrim.ad   offline livery "upload" (apply)
  livery2    PROJECTS\GT7SP\LIVERY2\LIVERY2.ADC                  Save takes the upload path; user
                                                                 decals allowed offline
  thumbnail  PROJECTS\GT7SP\THUMBNAIL\THUMBNAIL.ADC              no 10/32 KB thumbnail size floor
  gallery    PROJECTS\GT7SP\GALLERY\GALLERY.ADC                  "My Decals" listed offline
  grim       SCRIPTS\GT7SP\GRIM\GRIM.ADC           grim.ad       decal list from <game>\decals\*.svg
  components PROJECTS\GT7SP\COMPONENTS\COMPONENTS.ADC exitButton.ad Top Screen Exit saves and closes
                                                                 the game

Build: clone https://github.com/Nenkai/GTAdhocToolchain (3fecc2e) next to this folder as
..\GTAdhocToolchain, apply GTAdhocToolchain-exact-roundtrip.patch (symbol ids by name, so an
unchanged script round-trips byte for byte), clone https://github.com/Nenkai/GTToolsSharp (a59e6f1, with submodules) as ..\GTToolsSharp and
apply GTToolsSharp-read-only-open.patch (opens the volumes read-only and shared, so a running game
or a read-only dump folder works), then
`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.
