using System.Numerics;
using System.Diagnostics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Powder;

public sealed class SandboxApp
{
    public const int GridWidth = GameDefaults.Width, GridHeight = GameDefaults.Height;
    private const int CanvasX = 16, CanvasY = 104;
    private const int SidebarX = CanvasX + GridWidth * 2 + 24, SidebarWidth = 256;
    private const int WindowWidth = SidebarX + SidebarWidth + 24, WindowHeight = CanvasY + GridHeight * 2 + 72;
    private const int ConfirmX = (WindowWidth - 452) / 2, ConfirmY = (WindowHeight - 168) / 2;
    private static Rectangle ConfirmButton => new(ConfirmX + 120, ConfirmY + 104, 100, 36);
    private static Rectangle CancelButton => new(ConfirmX + 232, ConfirmY + 104, 100, 36);
    private static readonly Color Background = new(20, 22, 26, 255);
    private static readonly Color CanvasBackground = new(13, 15, 18, 255);
    private static readonly Color Panel = new(27, 30, 35, 255);
    private static readonly Color Border = new(44, 48, 55, 255);
    private static readonly Color Ink = new(224, 226, 231, 255);
    private static readonly Color Muted = new(132, 138, 148, 255);
    private static readonly Color Accent = new(105, 225, 199, 255);
    private static readonly (double T, Color C)[] HeatStops =
    [
        (-40, new(57, 91, 204, 255)), (0, new(72, 175, 220, 255)), (30, new(74, 191, 165, 255)),
        (100, new(222, 207, 91, 255)), (400, new(244, 131, 58, 255)),
        (850, new(236, 66, 70, 255)), (1500, new(255, 229, 211, 255))
    ];
    private readonly Color[] pixels = new Color[GridWidth * GridHeight];
    private Color[] airColors = [];
    private bool[] airColorReady = [];
    private World world = new(GridWidth, GridHeight);
    private Element selected = Element.Sand;
    private Font font;
    private Texture2D texture;
    private bool paused, thermal, help, showProperties, painting, showAirflow;
    private int radius = 5, speed = 1, tool; // 0: matter, 1: heat, 2: cool
    private Vector2 previous;
    private string toast = "";
    private double toastUntil;
    private double accumulator;
    private double physicsStepSeconds = .004;
    private World? renderedWorld;
    private long renderedRevision = -1;
    private bool renderedThermal, renderedAtmosphere;
    // Admit less simulation time under load, rather than running a long catch-up
    // batch that blocks mouse input. Every admitted step still has the same physical dt.
    private int FrameSteps => Math.Clamp((int)(.020 / Math.Max(.001, physicsStepSeconds)), 1, 8);
    private (double Mass, double Energy, int Particles) stats;
    private string scenePath = WorldFile.DefaultPath;
    private string? pendingAction;
    private double smokeSavedMass;

    public int Run(string[] args)
    {
        bool smoke = args.Contains("--smoke-test");
        int frames = ReadInt(args, "--frames", smoke ? 120 : 0);
        string? screenshot = ReadString(args, "--screenshot");
        string? frameReport = ReadString(args, "--frame-report");
        scenePath = ReadString(args, "--scene") ?? (smoke
            ? Path.Combine(Path.GetTempPath(), $"powder-lab-smoke-{Guid.NewGuid()}.json") : WorldFile.DefaultPath);
        bool initialized = false;
        try
        {
            SetConfigFlags(ConfigFlags.VSyncHint);
            SetTraceLogLevel(TraceLogLevel.Warning);
            InitWindow(WindowWidth, WindowHeight, "Powder Lab | Particle sandbox");
            initialized = true;
            SetExitKey(KeyboardKey.Escape);
            SetTargetFPS(60);
            string fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DejaVuSans.ttf");
            font = LoadFontEx(fontPath, 32, null, 0);
            SetTextureFilter(font.Texture, TextureFilter.Bilinear);
            Image image = GenImageColor(GridWidth, GridHeight, Background);
            texture = LoadTextureFromImage(image);
            UnloadImage(image);
            SetTextureFilter(texture, TextureFilter.Point);
            if (args.Contains("--air-demo"))
            {
                Scenes.Convection(world); thermal = true; showAirflow = true;
            }
            if (args.Contains("--boiling-demo")) Scenes.Boiling(world);
            if (args.Contains("--liquid-demo"))
            {
                Scenes.LiquidConvection(world); thermal = true; showAirflow = true;
            }
            stats = world.Statistics();
            if (smoke && stats.Particles != 0)
                throw new InvalidOperationException("Startup canvas must be empty.");
            int frame = 0;
            long loopStarted = Stopwatch.GetTimestamp(), physicsTicks = 0, renderTicks = 0, drawTicks = 0;
            long initialTick = world.Tick;
            while (!WindowShouldClose())
            {
                Input();
                if (smoke) SmokeActions(frame);
                double dt = Math.Min(GetFrameTime(), .1);
                accumulator = paused ? 0 : Math.Min(accumulator + dt * speed, World.FixedDelta * FrameSteps);
                long started = Stopwatch.GetTimestamp();
                int steps = 0;
                while (accumulator >= World.FixedDelta)
                {
                    world.Step(); accumulator -= World.FixedDelta; steps++;
                }
                if (frameReport is not null) physicsTicks += Stopwatch.GetTimestamp() - started;
                if (steps > 0) physicsStepSeconds = .5 * physicsStepSeconds + .5 * Stopwatch.GetElapsedTime(started).TotalSeconds / steps;
                if (frame % 15 == 0) stats = world.Statistics();
                if (renderedWorld != world || renderedRevision != world.VisualRevision
                    || renderedThermal != thermal || renderedAtmosphere != world.AtmosphereEnabled)
                {
                    long renderStarted = frameReport is not null ? Stopwatch.GetTimestamp() : 0;
                    RenderPixels(); renderedWorld = world; renderedRevision = world.VisualRevision;
                    renderedThermal = thermal; renderedAtmosphere = world.AtmosphereEnabled;
                    if (frameReport is not null) renderTicks += Stopwatch.GetTimestamp() - renderStarted;
                }
                if (smoke && frame == 0 && pixels.Any(c => !c.Equals(CanvasBackground)))
                    throw new InvalidOperationException("Startup canvas must have a plain background.");
                long drawStarted = frameReport is not null ? Stopwatch.GetTimestamp() : 0;
                BeginDrawing();
                ClearBackground(Background);
                Header();
                Canvas();
                Sidebar();
                Footer();
                if (help) HelpOverlay();
                if (pendingAction is not null) ConfirmOverlay();
                EndDrawing();
                if (frameReport is not null) drawTicks += Stopwatch.GetTimestamp() - drawStarted;
                frame++;
                if (frames > 0 && frame >= frames)
                {
                    if (screenshot is not null)
                    {
                        string full = Path.GetFullPath(screenshot);
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        Image capture = LoadImageFromScreen();
                        try
                        {
                            if (!ExportImage(capture, full)) throw new IOException("Could not export screenshot.");
                        }
                        finally { UnloadImage(capture); }
                    }
                    if (smoke)
                    {
                        if (!File.Exists(scenePath) || !thermal || !paused || tool != 0)
                            throw new InvalidOperationException("UI smoke actions did not complete.");
                        Console.WriteLine($"UI SMOKE PASS: {frame} frames; {world.Count} particles; empty startup, palette, painting, heat, erase, pause/step, thermal view, properties, save/load, clear, fractional boiling, submerged vapor, sub-boiling evaporation, vapor transparency and condensate layer transitions.");
                    }
                    break;
                }
            }
            if (frameReport is not null)
            {
                string full = Path.GetFullPath(frameReport);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, System.Text.Json.JsonSerializer.Serialize(new
                {
                    Frames = frame, PhysicsSteps = world.Tick - initialTick,
                    ElapsedSeconds = Stopwatch.GetElapsedTime(loopStarted).TotalSeconds,
                    PhysicsMilliseconds = physicsTicks * 1000.0 / Stopwatch.Frequency,
                    RenderMilliseconds = renderTicks * 1000.0 / Stopwatch.Frequency,
                    DrawAndWaitMilliseconds = drawTicks * 1000.0 / Stopwatch.Frequency
                }));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Powder Lab: {ex.Message}");
            Console.Error.WriteLine("Use a desktop session with OpenGL 3.3. Headless physics checks: dotnet run -- --self-test");
            return 1;
        }
        finally
        {
            if (initialized)
            {
                UnloadTexture(texture);
                UnloadFont(font);
                CloseWindow();
            }
        }
    }

    private void Input()
    {
        if (IsKeyPressed(KeyboardKey.F1)) help = !help;
        if (pendingAction is not null)
        {
            if (IsKeyPressed(KeyboardKey.Enter)) ConfirmAction();
            if (IsMouseButtonPressed(MouseButton.Left))
            {
                if (Hover(ConfirmButton)) ConfirmAction();
                else if (Hover(CancelButton)) pendingAction = null;
            }
            return;
        }
        if (help) return;
        bool ctrl = IsKeyDown(KeyboardKey.LeftControl) || IsKeyDown(KeyboardKey.RightControl);
        if (IsKeyPressed(KeyboardKey.Space)) paused = !paused;
        if (IsKeyPressed(KeyboardKey.N)) { paused = true; world.Step(); }
        if (IsKeyPressed(KeyboardKey.T)) thermal = !thermal;
        if (IsKeyPressed(KeyboardKey.I)) showProperties = !showProperties;
        if (IsKeyPressed(KeyboardKey.A)) showAirflow = !showAirflow;
        if (IsKeyPressed(KeyboardKey.B)) tool = 0;
        if (IsKeyPressed(KeyboardKey.H)) tool = 1;
        if (IsKeyPressed(KeyboardKey.J)) tool = 2;
        if (IsKeyPressed(KeyboardKey.C)) pendingAction = "clear";
        if (IsKeyPressed(KeyboardKey.R)) pendingAction = "demo";
        if (ctrl && IsKeyPressed(KeyboardKey.S)) Save();
        if (ctrl && IsKeyPressed(KeyboardKey.O)) Load();
        int[] numbers = [(int)KeyboardKey.One, (int)KeyboardKey.Two, (int)KeyboardKey.Three,
            (int)KeyboardKey.Four, (int)KeyboardKey.Five, (int)KeyboardKey.Six,
            (int)KeyboardKey.Seven, (int)KeyboardKey.Eight, (int)KeyboardKey.Nine];
        for (int i = 0; i < numbers.Length; i++)
            if (IsKeyPressed((KeyboardKey)numbers[i])) { selected = (Element)(i + 1); tool = 0; }
        radius = Math.Clamp(radius + (int)GetMouseWheelMove(), 1, 30);
        if (IsKeyPressed(KeyboardKey.LeftBracket)) radius = Math.Max(1, radius - 1);
        if (IsKeyPressed(KeyboardKey.RightBracket)) radius = Math.Min(30, radius + 1);
        if (IsMouseButtonPressed(MouseButton.Left)) Click(GetMousePosition());
        Vector2 pos = GetMousePosition();
        bool inside = InCanvas(pos);
        int x = (int)(pos.X - CanvasX) / 2, y = (int)(pos.Y - CanvasY) / 2;
        if (inside && IsMouseButtonPressed(MouseButton.Middle))
        {
            ref readonly var picked = ref world.At(x, y);
            selected = picked.Element == Element.Vapor ? picked.MeltOrigin : picked.Element; tool = 0;
        }
        bool draw = inside && (IsMouseButtonDown(MouseButton.Left) || IsMouseButtonDown(MouseButton.Right));
        if (pendingAction is not null) draw = false;
        if (draw)
        {
            var point = new Vector2(x, y);
            Vector2 start = painting ? previous : point;
            int distance = Math.Max(1, (int)Vector2.Distance(start, point));
            double strokeSeconds = Math.Min(GetFrameTime(), .1);
            if (!paused) strokeSeconds = Math.Min(strokeSeconds * speed,
                Math.Max(0, World.FixedDelta * FrameSteps - accumulator));
            // A continuous stroke does not leave gaps between frames.
            for (int n = 0; n <= distance; n++)
            {
                Vector2 p = Vector2.Lerp(start, point, (float)n / distance);
                Paint((int)p.X, (int)p.Y, IsMouseButtonDown(MouseButton.Right),
                    strokeSeconds / (distance + 1));
            }
            previous = point;
        }
        painting = draw;
    }

    private void Paint(int x, int y, bool erase = false, double seconds = World.FixedDelta)
    {
        if (erase) world.Brush(x, y, radius, Element.Empty, true);
        else if (tool == 0) world.Brush(x, y, radius, selected);
        else world.HeatBrush(x, y, radius, tool == 1 ? 20 : -20, seconds);
    }

    private enum Control
    {
        Pause, Step, Clear, Speed, Thermal, Save, Load, Help,
        Draw, Heat, Cool, Smaller, Larger, Properties, Atmosphere, Airflow
    }

    private static Rectangle Bounds(Control control) => control switch
    {
        Control.Pause => new(176, 18, 64, 28),
        Control.Step => new(248, 18, 48, 28),
        Control.Clear => new(304, 18, 56, 28),
        Control.Speed => new(368, 18, 48, 28),
        Control.Thermal => new(424, 18, 104, 28),
        Control.Save => new(WindowWidth - 256, 18, 64, 28),
        Control.Load => new(WindowWidth - 184, 18, 64, 28),
        Control.Help => new(WindowWidth - 112, 18, 96, 28),
        Control.Draw => new(SidebarX, 400, 80, 30),
        Control.Heat => new(SidebarX + 88, 400, 80, 30),
        Control.Cool => new(SidebarX + 176, 400, 80, 30),
        Control.Smaller => new(SidebarX, 444, 30, 28),
        Control.Larger => new(SidebarX + 226, 444, 30, 28),
        Control.Properties => new(SidebarX, 494, 116, 28),
        Control.Atmosphere => new(SidebarX, 260, SidebarWidth, 28),
        Control.Airflow => new(SidebarX + 132, 494, 124, 28),
        _ => throw new ArgumentOutOfRangeException(nameof(control))
    };

    private static Rectangle MaterialBounds(int index)
        => new(SidebarX + index % 2 * 132, 128 + index / 2 * 28, 124, 26);

    private void Click(Vector2 point)
    {
        foreach (Control control in Enum.GetValues<Control>())
        {
            if (control == Control.Atmosphere && !showProperties) continue;
            if (!CheckCollisionPointRec(point, Bounds(control))) continue;
            switch (control)
            {
                case Control.Pause: paused = !paused; break;
                case Control.Step: paused = true; world.Step(); break;
                case Control.Clear: pendingAction = "clear"; break;
                case Control.Speed: speed = speed == 1 ? 2 : speed == 2 ? 4 : 1; break;
                case Control.Thermal: thermal = !thermal; break;
                case Control.Save: Save(); break;
                case Control.Load: Load(); break;
                case Control.Help: help = !help; break;
                case Control.Draw: tool = 0; break;
                case Control.Heat: tool = 1; break;
                case Control.Cool: tool = 2; break;
                case Control.Smaller: radius = Math.Max(1, radius - 1); break;
                case Control.Larger: radius = Math.Min(30, radius + 1); break;
                case Control.Properties: showProperties = !showProperties; break;
                case Control.Atmosphere: world.AtmosphereEnabled = !world.AtmosphereEnabled; break;
                case Control.Airflow: showAirflow = !showAirflow; break;
            }
            return;
        }
        if (showProperties) return;
        for (int i = 0; i < 15; i++)
            if (CheckCollisionPointRec(point, MaterialBounds(i)))
            { selected = (Element)(i == 14 ? 0 : i + 1); tool = 0; return; }
    }

    private void Save()
    {
        try { WorldFile.Save(world, scenePath); Notify("Scene saved. Ctrl+O to restore."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Notify("Could not save: " + ex.Message); }
    }

    private void Load()
    {
        try { world = WorldFile.Load(scenePath, GridWidth, GridHeight); stats = world.Statistics(); Notify("Scene restored."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Notify("Could not load: " + ex.Message); }
    }

    private void ConfirmAction()
    {
        if (pendingAction == "demo") Scenes.Demo(world); else world.Clear();
        stats = world.Statistics(); accumulator = 0; pendingAction = null;
    }

    private void Header()
    {
        DrawRectangle(16, 25, 6, 14, Accent);
        Text("Powder Lab", 32, 22, 20, Ink);
        ControlButton(Control.Pause, paused ? "Resume" : "Pause", paused);
        ControlButton(Control.Step, "Step");
        ControlButton(Control.Clear, "Clear");
        ControlButton(Control.Speed, $"{speed}x", speed != 1);
        ControlButton(Control.Thermal, "Temperature", thermal);
        ControlButton(Control.Save, "Save");
        ControlButton(Control.Load, "Load");
        ControlButton(Control.Help, "Help  F1");

        Particle particle = default;
        Vector2 mouse = GetMousePosition();
        if (InCanvas(mouse) && !help && pendingAction is null)
        {
            int i = (int)(mouse.Y - CanvasY) / 2 * GridWidth + (int)(mouse.X - CanvasX) / 2;
            particle = world.Bubbles[i].Element != Element.Empty ? world.Bubbles[i] : world.Cells[i];
        }
        bool hasParticle = particle.Element != Element.Empty;
        bool hasAir = !hasParticle && InCanvas(mouse) && !help && pendingAction is null && world.AtmosphereEnabled;
        double airTemperature = hasAir ? world.Air.TemperatureAtFine((int)(mouse.X - CanvasX) / 2,
            (int)(mouse.Y - CanvasY) / 2) : 20;
        Text("Under cursor", 16, 58, 9, Muted);
        Text("Temperature", 176, 58, 9, Muted);
        Text("Mass", 304, 58, 9, Muted);
        Text(hasParticle ? Materials.Name(particle) : hasAir ? "Air" : "--", 16, 74, 12, Ink);
        Text(hasParticle ? $"{particle.Temperature:F1} C" : hasAir ? $"{airTemperature:F1} C" : "--", 176, 74, 12, Ink);
        string massLabel = particle.Mass >= 1e-6 ? $"{particle.Mass * 1000:F3} g"
            : $"{particle.Mass * 1e6:F3} mg";
        Text(hasParticle ? massLabel : "--", 304, 74, 12, Ink);
    }

    private void Canvas()
    {
        DrawTextureEx(texture, new(CanvasX, CanvasY), 0, 2, Color.White);
        DrawLine(CanvasX, CanvasY + GridHeight * 2 + 1, CanvasX + GridWidth * 2, CanvasY + GridHeight * 2 + 1, Border);
        if (showAirflow) DrawFlow();
        Vector2 mouse = GetMousePosition();
        if (!InCanvas(mouse) || help || pendingAction is not null) return;
        int x = (int)(mouse.X - CanvasX) / 2, y = (int)(mouse.Y - CanvasY) / 2;
        DrawCircleLines(CanvasX + x * 2 + 1, CanvasY + y * 2 + 1, radius * 2, new(220, 224, 230, 130));
    }

    private void DrawFlow()
    {
        // Liquid speeds are much lower than air speeds; arrows use separate visual scales.
        for (int y = 8; y < GridHeight; y += 12)
        for (int x = 8; x < GridWidth; x += 12)
        {
            if (LiquidProperties.Kind(world.At(x, y)) == 0) continue;
            var v = world.Liquids.VelocityAtFine(x, y);
            DrawFlowArrow(new(CanvasX + x * 2 + 1, CanvasY + y * 2 + 1), v.X, v.Y, 600, new(216, 240, 255, 200));
        }
        if (!world.AtmosphereEnabled) return;
        var air = world.Air;
        for (int y = 2; y < air.Height; y += 4)
        for (int x = 2; x < air.Width; x += 4)
        {
            if (air.Blocked[y * air.Width + x]) continue;
            var v = air.Velocity((x + .5) * air.Dx, (y + .5) * air.Dy);
            Vector2 start = new(CanvasX + (float)((x + .5) * air.Dx / World.CellSize * 2),
                CanvasY + (float)((y + .5) * air.Dy / World.CellSize * 2));
            DrawFlowArrow(start, v.X, v.Y, 40, new(130, 208, 195, 155));
        }
    }

    private static void DrawFlowArrow(Vector2 start, double vx, double vy, float scale, Color color)
    {
        var vector = new Vector2((float)vx, (float)vy);
        float length = Math.Min(18, vector.Length() * scale);
        if (length < 2) return;
        Vector2 direction = Vector2.Normalize(vector), end = start + direction * length;
        DrawLineEx(start, end, 1, color);
        var normal = new Vector2(-direction.Y, direction.X);
        DrawLineEx(end, end - direction * 3 + normal * 2, 1, color);
        DrawLineEx(end, end - direction * 3 - normal * 2, 1, color);
    }

    private void Sidebar()
    {
        Text(showProperties ? "Material properties" : "Materials", SidebarX, CanvasY, 13, Muted);
        if (showProperties)
        {
            var material = Materials.Get(selected);
            Text(material.Name, SidebarX, 132, 16, Ink);
            Property("Density", $"{material.Density:N0} kg/m3", 163);
            Property("Heat capacity", $"{material.SpecificHeat:N0} J/kg K", 186);
            Property("Conductivity", $"{material.Conductivity:0.###} W/m K", 209);
            Element origin = selected == Element.Lava ? Element.Stone : Materials.IsWater(selected) ? Element.Water : selected;
            if (origin == Element.Water || VaporProperties.Supports(origin))
                Property("Boiling point", $"{VaporProperties.BoilingPoint(origin):N0} C", 232);
            ControlButton(Control.Atmosphere, "Atmosphere", world.AtmosphereEnabled);
            Wrapped(material.Description, SidebarX, 308, SidebarWidth, 11, Muted);
        }
        else for (int i = 0; i < 15; i++)
        {
            Element element = (Element)(i == 14 ? 0 : i + 1);
            var material = Materials.Get(element);
            Rectangle bounds = MaterialBounds(i);
            bool active = selected == element && tool == 0;
            if (active || Hover(bounds))
                DrawRectangleRounded(bounds, .15f, 4, active ? new(37, 52, 49, 255) : Panel);
            Color color = new(material.Color.R, material.Color.G, material.Color.B, (byte)255);
            if (element == Element.Empty)
                DrawRectangleLines((int)bounds.X + 10, (int)bounds.Y + 9, 8, 8, Muted);
            else DrawRectangle((int)bounds.X + 10, (int)bounds.Y + 9, 8, 8, color);
            Text(material.Name, bounds.X + 28, bounds.Y + 6, 13, active ? Accent : Ink);
        }
        DrawLine(SidebarX, 368, SidebarX + SidebarWidth, 368, Border);
        Text("Brush", SidebarX, 380, 12, Muted);
        ControlButton(Control.Draw, "Draw", tool == 0);
        ControlButton(Control.Heat, "Heat", tool == 1);
        ControlButton(Control.Cool, "Cool", tool == 2);
        ControlButton(Control.Smaller, "-");
        string size = $"{radius * 2 + 1} cells";
        float sizeWidth = MeasureTextEx(font, size, 12, 0).X;
        Text(size, SidebarX + SidebarWidth / 2 - sizeWidth / 2, 452, 12, Ink);
        ControlButton(Control.Larger, "+");
        ControlButton(Control.Properties, showProperties ? "Materials  I" : "Properties  I", showProperties);
        ControlButton(Control.Airflow, "Flow  A", showAirflow);
    }

    private void Footer()
    {
        int y = CanvasY + GridHeight * 2 + 16;
        Text($"{stats.Particles:N0} particles", 16, y, 11, Muted);
        if (stats.Particles > 0) Text($"{stats.Mass * 1000:N1} g", 156, y, 11, Muted);
        Text($"{GetFPS()} fps", CanvasX + GridWidth * 2 - 52, y, 11, Muted);
        if (thermal)
        {
            Text("-40", 300, y, 10, Muted);
            for (int i = 0; i < 128; i++) DrawRectangle(330 + i, y + 3, 1, 4, HeatColor(-40 + i / 127.0 * 1540));
            Text("1500+ C", 466, y, 10, Muted);
        }
        if (GetTime() < toastUntil) Text(toast, 16, y + 26, 11, Accent);
        else Text("LMB draw   /   RMB erase   /   Scroll brush size   /   Space pause", 16, y + 26, 11, Muted);
    }

    private void RenderPixels()
    {
        if (thermal && world.AtmosphereEnabled)
        {
            int count = world.Air.ControlVolumeCount;
            if (airColors.Length < count)
            {
                int n = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, count));
                airColors = new Color[n]; airColorReady = new bool[n];
            }
            Array.Clear(airColorReady, 0, count);
        }
        for (int i = 0; i < pixels.Length; i++)
        {
            ref readonly var p = ref world.Cells[i];
            if (p.Element == Element.Empty)
            {
                if (!thermal || !world.AtmosphereEnabled) { pixels[i] = CanvasBackground; continue; }
                int x = i % GridWidth, y = i / GridWidth, volume = world.Air.VolumeIndexAtFine(x, y);
                if (volume < 0) { pixels[i] = HeatColor(world.Air.AmbientTemperature); continue; }
                if (!airColorReady[volume])
                {
                    airColors[volume] = HeatColor(world.Air.TemperatureAtFine(x, y));
                    airColorReady[volume] = true;
                }
                pixels[i] = airColors[volume];
                continue;
            }
            if (thermal) { pixels[i] = HeatColor(p.Temperature); continue; }
            Rgb rgb = ParticleAppearance.VariedColor(p.Element, p.Variation);
            Color c = new(rgb.R, rgb.G, rgb.B, (byte)255);
            if (p.Element is Element.Fire or Element.Lava)
            {
                float flicker = (float)(.5 + .5 * Math.Sin(world.Tick * .22 + p.Variation));
                c = p.Element == Element.Fire
                    ? new((byte)255, (byte)(115 + 115 * flicker), (byte)(30 + 80 * flicker), (byte)255)
                    : new((byte)245, (byte)(70 + 65 * flicker), (byte)32, (byte)255);
            }
            else
            {
                double temperature = p.Temperature;
                if (temperature > 525)
                {
                    float glow = (float)Math.Clamp((temperature - 525) / 1100, 0, .85);
                    c = Mix(c, new(255, 125, 54, 255), glow);
                }
            }
            float opacity = ParticleAppearance.Opacity(p);
            pixels[i] = opacity >= 1 ? c : Mix(CanvasBackground, c, opacity);
        }
        foreach (int i in world.BubbleLocations)
        {
            ref readonly var bubble = ref world.Bubbles[i];
            if (bubble.Element == Element.Empty) continue;
            Rgb c = ParticleAppearance.VariedColor(bubble.Element, bubble.Variation);
            pixels[i] = thermal ? HeatColor(bubble.Temperature)
                : Mix(pixels[i], new Color(c.R, c.G, c.B, (byte)255), ParticleAppearance.Opacity(bubble));
        }
        UpdateTexture(texture, pixels);
    }

    private static Color HeatColor(double t)
    {
        var stops = HeatStops;
        for (int i = 1; i < stops.Length; i++)
            if (t <= stops[i].T) return Mix(stops[i - 1].C, stops[i].C,
                (float)Math.Clamp((t - stops[i - 1].T) / (stops[i].T - stops[i - 1].T), 0, 1));
        return stops[^1].C;
    }

    private static Color Mix(Color a, Color b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t), (byte)255);

    private void HelpOverlay()
    {
        int x = (WindowWidth - 616) / 2, y = (WindowHeight - 488) / 2;
        DrawRectangle(0, 0, WindowWidth, WindowHeight, new(0, 0, 0, 185));
        DrawRectangleRounded(new(x, y, 616, 488), .05f, 6, Panel);
        Text("YOUR PARTICLE LABORATORY", x + 35, y + 34, 24, Ink);
        Text("Every cell is a 2 x 2 pixel piece of matter.", x + 35, y + 72, 14, Accent);
        string[] lines = ["Left mouse: draw     Right mouse: erase     Middle mouse: pick",
            "Mouse wheel or [ / ]: change brush radius",
            "B: draw     H: heat     J: cool     1-9: quick material selection",
            "Space: pause     N: single step     T: temperature     A: flow",
            "Ctrl+S: save scene     Ctrl+O: load scene",
            "C: clear canvas     R: load demo     I: material properties",
            "F1: close this guide     Esc: quit the game"];
        for (int i = 0; i < lines.Length; i++) Text(lines[i], x + 35, y + 117 + i * 31, 13, Ink);
        Wrapped("Try heating water, melting ice, dropping sand into a liquid, or igniting wood. Hover over matter to inspect its mass and temperature.",
            x + 35, y + 353, 540, 14, Muted);
        Text("A physics sandbox. See README for the model's limits.", x + 35, y + 446, 12, Accent);
    }

    private void ConfirmOverlay()
    {
        DrawRectangle(0, 0, WindowWidth, WindowHeight, new(0, 0, 0, 160));
        DrawRectangleRounded(new(ConfirmX, ConfirmY, 452, 168), .08f, 6, Panel);
        Text(pendingAction == "demo" ? "Replace scene with the demo?" : "Clear the entire canvas?", ConfirmX + 31, ConfirmY + 24, 20, Ink);
        Text("Save your current scene first with Ctrl+S.", ConfirmX + 31, ConfirmY + 62, 13, Muted);
        Button(ConfirmButton, "Confirm", true);
        Button(CancelButton, "Cancel", false);
    }

    private void Property(string name, string value, int y)
    {
        Text(name, SidebarX, y, 11, Muted);
        float width = MeasureTextEx(font, value, 11, 0).X;
        Text(value, SidebarX + SidebarWidth - width, y, 11, Ink);
    }

    private void ControlButton(Control control, string label, bool active = false)
        => Button(Bounds(control), label, active);

    private void Button(Rectangle r, string label, bool active)
    {
        DrawRectangleRounded(r, .15f, 4, active ? new(37, 52, 49, 255)
            : Hover(r) ? new(38, 42, 49, 255) : Panel);
        Vector2 size = MeasureTextEx(font, label, 11, 0);
        Text(label, r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2, 11, active ? Accent : Ink);
    }

    private void Wrapped(string text, int x, int y, int width, int size, Color color)
    {
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (MeasureTextEx(font, next, size, 0).X > width && line.Length > 0)
            { Text(line, x, y, size, color); y += size + 4; line = word; }
            else line = next;
        }
        Text(line, x, y, size, color);
    }

    private void Text(string text, float x, float y, int size, Color color)
        => DrawTextEx(font, text, new(x, y), size, 0, color);
    private static bool Hover(Rectangle r) => CheckCollisionPointRec(GetMousePosition(), r);
    private static bool InCanvas(Vector2 p) => p.X >= CanvasX && p.X < CanvasX + GridWidth * 2
        && p.Y >= CanvasY && p.Y < CanvasY + GridHeight * 2;
    private void Notify(string message) { toast = message; toastUntil = GetTime() + 5; }
    private static string? ReadString(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    private static int ReadInt(string[] args, string key, int fallback)
        => int.TryParse(ReadString(args, key), out int value) && value > 0 ? value : fallback;

    private void SmokeActions(int frame)
    {
        void Press(Control control)
        {
            Rectangle bounds = Bounds(control);
            Click(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        }
        void Pick(int index)
        {
            if (showProperties) Press(Control.Properties);
            Rectangle bounds = MaterialBounds(index);
            Click(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        }
        if (frame == 5)
        {
            Pick(1); Paint(140, 30);
            if (selected != Element.Water || world.Count == 0)
                throw new InvalidOperationException("Material selection or painting failed.");
        }
        if (frame == 10)
        {
            Pick(7); Paint(110, 30);
            double before = world.At(110, 30).Temperature;
            Press(Control.Heat); Paint(110, 30);
            if (world.At(110, 30).Temperature <= before)
                throw new InvalidOperationException("Heat brush failed.");
        }
        if (frame == 15)
        {
            Press(Control.Draw); Paint(170, 30); Paint(170, 30, true);
            if (world.At(170, 30).Element != Element.Empty)
                throw new InvalidOperationException("Erase brush failed.");
        }
        if (frame == 20) Press(Control.Thermal);
        if (frame == 30)
        {
            Press(Control.Save); smokeSavedMass = world.Statistics().Mass;
        }
        if (frame == 35)
        {
            Press(Control.Clear); ConfirmAction();
            if (world.Count != 0) throw new InvalidOperationException("Clear canvas failed.");
            Press(Control.Load);
            if (Math.Abs(world.Statistics().Mass - smokeSavedMass) > 1e-12)
                throw new InvalidOperationException("UI failed to restore the saved scene.");
        }
        if (frame == 40)
        {
            Press(Control.Properties);
            if (!showProperties) throw new InvalidOperationException("Properties panel failed to open.");
            Press(Control.Atmosphere);
            if (world.AtmosphereEnabled) throw new InvalidOperationException("Atmosphere toggle failed.");
        }
        if (frame == 50) Press(Control.Pause);
        if (frame == 55)
        {
            long before = world.Tick;
            Press(Control.Step);
            if (!paused || world.Tick != before + 1)
                throw new InvalidOperationException("Single step failed.");
            Press(Control.Properties);
        }
        if (frame == 60)
        {
            Press(Control.Properties); Press(Control.Atmosphere); Press(Control.Properties);
            Press(Control.Airflow);
            if (!showAirflow || !world.AtmosphereEnabled) throw new InvalidOperationException("Air display failed.");
            SetMousePosition(CanvasX + 110 * 2 + 1, CanvasY + 30 * 2 + 1);
        }
        if (frame == 70)
        {
            Pick(4); Press(Control.Properties);
            world.Set(200, 30, Element.Oil, 340);
            ref var oil = ref world.At(200, 30);
            double q = Thermodynamics.EnergyAtTemperature(oil, 370) - oil.Energy;
            world.HeatBrush(200, 30, 0, 20, q / 20);
            if (oil.Element != Element.Vapor || oil.MeltOrigin != Element.Oil || Materials.Name(oil) != "Oil vapor")
                throw new InvalidOperationException("Oil evaporation or vapor inspection failed.");
            SetMousePosition(CanvasX + 200 * 2 + 1, CanvasY + 30 * 2 + 1);
        }
        if (frame == 80)
        {
            Press(Control.Save); Press(Control.Clear); ConfirmAction(); Press(Control.Load);
            if (world.At(200, 30).Element != Element.Vapor || world.At(200, 30).MeltOrigin != Element.Oil)
                throw new InvalidOperationException("UI failed to restore oil vapor.");
        }
        if (frame == 90)
        {
            Pick(1); world.Set(200, 80, Element.Water, 100);
            double q = world.At(200, 80).Mass * Thermodynamics.Vaporization * .01;
            world.HeatBrush(200, 80, 0, 1, q); Press(Control.Step);
            if (!world.Bubbles.Any(p => p.Element == Element.Steam))
                throw new InvalidOperationException("Partial boiling did not create a submerged bubble.");
            SetMousePosition(CanvasX + 200 * 2 + 1, CanvasY + 80 * 2 + 1);
        }
        if (frame == 100)
        {
            Press(Control.Save); Press(Control.Clear); ConfirmAction(); Press(Control.Load);
            if (!world.Bubbles.Any(p => p.Element == Element.Steam))
                throw new InvalidOperationException("UI failed to restore submerged vapor.");
        }
        if (frame == 110)
        {
            world.Set(239, 130, Element.Glass, 90); world.Set(241, 130, Element.Glass, 90);
            world.Set(240, 131, Element.Glass, 90); world.Set(240, 130, Element.Water, 90);
            Press(Control.Step);
            ref readonly var vapor = ref world.At(240, 129);
            if (vapor.Element != Element.Steam || vapor.CondensationOffset <= 0 || vapor.Temperature >= 100)
                throw new InvalidOperationException("Sub-boiling surface evaporation did not create cool vapor.");
            bool previousThermal = thermal; thermal = false;
            RenderPixels();
            Color pixel = pixels[129 * GridWidth + 240];
            Rgb opaque = ParticleAppearance.VariedColor(vapor.Element, vapor.Variation);
            if (pixel.Equals(CanvasBackground) || pixel.Equals(new Color(opaque.R, opaque.G, opaque.B, (byte)255)))
                throw new InvalidOperationException("Dilute vapor was invisible or fully opaque.");
            thermal = previousThermal; RenderPixels();
            Press(Control.Save); Press(Control.Load);
            if (world.At(240, 129).CondensationOffset <= 0)
                throw new InvalidOperationException("UI failed to restore dilute vapor.");
        }
        if (frame == 115) CheckCondensatePixels();
    }

    private void CheckCondensatePixels()
    {
        const int x = 280, y = 150, index = y * GridWidth + x;
        var savedCell = world.Cells[index]; var savedBubble = world.Bubbles[index];
        bool savedThermal = thermal;
        try
        {
            thermal = false;
            foreach (var element in new[] { Element.Water, Element.Ice, Element.Steam })
            {
                world.Set(x, y, element);
                var full = world.Cells[index];
                Color previous = default;
                foreach (double fill in new[] { .000001, .001, .049, .051, .5, 1.0 })
                {
                    var drop = full; drop.Mass *= fill; drop.Energy *= fill;
                    world.Cells[index] = drop; world.Bubbles[index] = default;
                    world.RebuildBubbleIndices(); RenderPixels();
                    Color main = pixels[index];
                    world.Cells[index] = default; world.Bubbles[index] = drop;
                    world.RebuildBubbleIndices(); RenderPixels();
                    if (!main.Equals(pixels[index]))
                        throw new InvalidOperationException("Condensate brightness changes between particle layers.");
                    if (fill < .01 && ColorDifference(main, CanvasBackground) > 3)
                        throw new InvalidOperationException("Microscopic condensate flashes as a bright water pixel.");
                    if (fill == .051 && ColorDifference(main, previous) > 3)
                        throw new InvalidOperationException("Condensate flashes at the resolution threshold.");
                    previous = main;
                }
            }
        }
        finally
        {
            world.Cells[index] = savedCell; world.Bubbles[index] = savedBubble;
            world.RebuildBubbleIndices(); thermal = savedThermal; RenderPixels();
        }
    }

    private static int ColorDifference(Color a, Color b)
        => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
