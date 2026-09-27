QuickNotes Sample Application Assets
=====================================

This folder contains:

1. icon.ico - Application icon (16 to 256 px). The sample uses the Instella logo
   (a copy of assets/logo/instella.ico).

To create an icon:
- Use any icon editor (e.g., GIMP, Photoshop, online ico converter)
- Include multiple sizes for best display on different DPIs
- Save as icon.ico

For testing without an icon:
- Remove the <ApplicationIcon> line from SampleApp.csproj
- Remove the .WithIcon("Assets/icon.ico") line from Program.cs
