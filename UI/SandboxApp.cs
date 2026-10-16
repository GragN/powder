using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Powder;

public sealed class SandboxApp
{
    public const int GridWidth = 480, GridHeight = 336;
    private const int WindowWidth = 1280, WindowHeight = 800, CanvasX = 16, CanvasY = 64;
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
    private World world = new(GridWidth, GridHeight);
    private Element selected = Element.Sand;
    private Font font;
    private Texture2D texture;
    private bool paused, thermal, help, showProperties, painting;
    private int radius = 5, speed = 1, tool; // 0: matter, 1: heat, 2: cool
    private Vector2 previous;
    private string toast = "";
    private double toastUntil;
    private double accumulator;
    private (double Mass, double Energy, int Particles) stats;
    private string scenePath = WorldFile.DefaultPath;
    private string? pendingAction;
    private double smokeSavedMass;

    public int Run(string[] args)
    {
        bool smoke = args.Contains("--smoke-test");
        int frames = ReadInt(args, "--frames", smoke ? 120 : 0);
        string? screenshot = ReadString(args, "--screenshot");
        scenePath = ReadString(args, "--scene") ?? (smoke
            ? Path.Combine(Path.GetTempPath(), $"powder-lab-smoke-{Guid.NewGuid()}.json") : WorldFile.DefaultPath);
        bool initialized = false;
        try
        {
            SetConfigFlags(ConfigFlags.VSyncHint);
            SetTraceLogLevel(TraceLogLevel.Warning);
            InitWindow(WindowWidth, WindowHeight, "Powder Lab | Particle sandbox");
            initialized = true;
            SetExitKey(KeyboardKey.Null);
            SetTargetFPS(60);
            string fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DejaVuSans.ttf");
            font = LoadFontEx(fontPath, 32, null, 0);
            SetTextureFilter(font.Texture, TextureFilter.Bilinear);
            Image image = GenImageColor(GridWidth, GridHeight, Background);
            texture = LoadTextureFromImage(image);
            UnloadImage(image);
            SetTextureFilter(texture, TextureFilter.Point);
            stats = world.Statistics();
            if (smoke && stats.Particles != 0)
                throw new InvalidOperationException("Startup canvas must be empty.");
            int frame = 0;
            while (!WindowShouldClose())
            {
                Input();
                if (smoke) SmokeActions(frame);
                double dt = Math.Min(GetFrameTime(), .1);
                accumulator = paused ? 0 : Math.Min(accumulator + dt * speed, World.FixedDelta * 8);
                while (accumulator >= World.FixedDelta)
                {
                    world.Step(); accumulator -= World.FixedDelta;
                }
                if (frame % 15 == 0) stats = world.Statistics();
                RenderPixels();
                if (smoke && frame == 0 && pixels.Any(c => !c.Equals(CanvasBackground)))
                    throw new InvalidOperationException("Startup canvas must have a plain background.");
                BeginDrawing();
                ClearBackground(Background);
                Header();
                Canvas();
                Sidebar();
                Footer();
                if (help) HelpOverlay();
                if (pendingAction is not null) ConfirmOverlay();
                EndDrawing();
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
                        Console.WriteLine($"UI SMOKE PASS: {frame} frames; {world.Count} particles; empty startup, palette, painting, heat, erase, pause/step, thermal view, properties, save/load, clear.");
                    }
                    break;
                }
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
        if (IsKeyPressed(KeyboardKey.Escape)) { help = false; pendingAction = null; }
        if (pendingAction is not null)
        {
            if (IsKeyPressed(KeyboardKey.Enter)) ConfirmAction();
            if (IsMouseButtonPressed(MouseButton.Left))
            {
                if (Hover(new(534, 425, 100, 36))) ConfirmAction();
                else if (Hover(new(646, 425, 100, 36))) pendingAction = null;
            }
            return;
        }
        if (help) return;
        bool ctrl = IsKeyDown(KeyboardKey.LeftControl) || IsKeyDown(KeyboardKey.RightControl);
        if (IsKeyPressed(KeyboardKey.Space)) paused = !paused;
        if (IsKeyPressed(KeyboardKey.N)) { paused = true; world.Step(); }
        if (IsKeyPressed(KeyboardKey.T)) thermal = !thermal;
        if (IsKeyPressed(KeyboardKey.I)) showProperties = !showProperties;
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
            selected = world.At(x, y).Element; tool = 0;
        }
        bool draw = inside && (IsMouseButtonDown(MouseButton.Left) || IsMouseButtonDown(MouseButton.Right));
        if (pendingAction is not null) draw = false;
        if (draw)
        {
            var point = new Vector2(x, y);
            Vector2 start = painting ? previous : point;
            int distance = Math.Max(1, (int)Vector2.Distance(start, point));
            // A continuous stroke does not leave gaps between frames.
            for (int n = 0; n <= distance; n++)
            {
                Vector2 p = Vector2.Lerp(start, point, (float)n / distance);
                Paint((int)p.X, (int)p.Y, IsMouseButtonDown(MouseButton.Right));
            }
            previous = point;
        }
        painting = draw;
    }

    private void Paint(int x, int y, bool erase = false)
    {
        if (erase) world.Brush(x, y, radius, Element.Empty, true);
        else if (tool == 0) world.Brush(x, y, radius, selected);
        else world.HeatBrush(x, y, radius, tool == 1 ? 8 : -8);
    }

    private enum Control
    {
        Pause, Step, Clear, Speed, Thermal, Save, Load, Help,
        Draw, Heat, Cool, Smaller, Larger, Properties, AirCooling
    }

    private static Rectangle Bounds(Control control) => control switch
    {
        Control.Pause => new(224, 18, 80, 28),
        Control.Step => new(312, 18, 56, 28),
        Control.Clear => new(376, 18, 64, 28),
        Control.Speed => new(448, 18, 56, 28),
        Control.Thermal => new(512, 18, 112, 28),
        Control.Save => new(1040, 18, 64, 28),
        Control.Load => new(1112, 18, 64, 28),
        Control.Help => new(1184, 18, 80, 28),
        Control.Draw => new(1000, 408, 80, 30),
        Control.Heat => new(1088, 408, 80, 30),
        Control.Cool => new(1176, 408, 80, 30),
        Control.Smaller => new(1000, 452, 30, 28),
        Control.Larger => new(1226, 452, 30, 28),
        Control.Properties => new(1000, 502, 116, 28),
        Control.AirCooling => new(1000, 662, 256, 28),
        _ => throw new ArgumentOutOfRangeException(nameof(control))
    };

    private static Rectangle MaterialBounds(int index)
        => new(1000 + index % 2 * 132, 100 + index / 2 * 32, 124, 28);

    private void Click(Vector2 point)
    {
        foreach (Control control in Enum.GetValues<Control>())
        {
            if (control == Control.AirCooling && !showProperties) continue;
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
                case Control.AirCooling: world.AmbientCooling = !world.AmbientCooling; break;
            }
            return;
        }
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
    }

    private void Canvas()
    {
        DrawTextureEx(texture, new(CanvasX, CanvasY), 0, 2, Color.White);
        DrawRectangleLines(CanvasX - 1, CanvasY - 1, GridWidth * 2 + 2, GridHeight * 2 + 2, Border);
        Vector2 mouse = GetMousePosition();
        if (!InCanvas(mouse) || help || pendingAction is not null) return;
        int x = (int)(mouse.X - CanvasX) / 2, y = (int)(mouse.Y - CanvasY) / 2;
        DrawCircleLines(CanvasX + x * 2 + 1, CanvasY + y * 2 + 1, radius * 2, new(220, 224, 230, 130));
        var particle = world.At(x, y);
        if (particle.Element == Element.Empty) return;
        string detail = $"{Materials.Get(particle.Element).Name}   {particle.Temperature:F1} C   {particle.Mass * 1000:F3} g";
        float width = MeasureTextEx(font, detail, 12, 0).X + 24;
        float tx = Math.Clamp(mouse.X + 18, CanvasX + 4, CanvasX + GridWidth * 2 - width - 4);
        float ty = Math.Clamp(mouse.Y + 20, CanvasY + 4, CanvasY + GridHeight * 2 - 32);
        DrawRectangleRounded(new(tx, ty, width, 28), .15f, 4, Panel);
        Text(detail, tx + 12, ty + 7, 12, Ink);
    }

    private void Sidebar()
    {
        Text("Materials", 1000, 72, 13, Muted);
        for (int i = 0; i < 15; i++)
        {
            Element element = (Element)(i == 14 ? 0 : i + 1);
            var material = Materials.Get(element);
            Rectangle bounds = MaterialBounds(i);
            bool active = selected == element && tool == 0;
            if (active || Hover(bounds))
                DrawRectangleRounded(bounds, .15f, 4, active ? new(37, 52, 49, 255) : Panel);
            Color color = new(material.Color.R, material.Color.G, material.Color.B, (byte)255);
            if (element == Element.Empty)
                DrawRectangleLines((int)bounds.X + 10, (int)bounds.Y + 10, 8, 8, Muted);
            else DrawRectangle((int)bounds.X + 10, (int)bounds.Y + 10, 8, 8, color);
            Text(material.Name, bounds.X + 28, bounds.Y + 7, 13, active ? Accent : Ink);
        }
        DrawLine(1000, 376, 1256, 376, Border);
        Text("Brush", 1000, 388, 12, Muted);
        ControlButton(Control.Draw, "Draw", tool == 0);
        ControlButton(Control.Heat, "Heat", tool == 1);
        ControlButton(Control.Cool, "Cool", tool == 2);
        ControlButton(Control.Smaller, "-");
        string size = $"{radius * 2 + 1} cells";
        float sizeWidth = MeasureTextEx(font, size, 12, 0).X;
        Text(size, 1128 - sizeWidth / 2, 460, 12, Ink);
        ControlButton(Control.Larger, "+");
        ControlButton(Control.Properties, "Properties  I", showProperties);
        if (!showProperties) return;
        var selectedMaterial = Materials.Get(selected);
        Text(selectedMaterial.Name, 1000, 547, 16, Ink);
        Property("Density", $"{selectedMaterial.Density:N0} kg/m3", 577);
        Property("Heat capacity", $"{selectedMaterial.SpecificHeat:N0} J/kg K", 600);
        Property("Conductivity", $"{selectedMaterial.Conductivity:0.###} W/m K", 623);
        ControlButton(Control.AirCooling, "Ambient cooling / 20 C", world.AmbientCooling);
        Wrapped(selectedMaterial.Description, 1000, 704, 256, 11, Muted);
    }

    private void Footer()
    {
        Text($"{stats.Particles:N0} particles", 16, 753, 11, Muted);
        if (stats.Particles > 0) Text($"{stats.Mass * 1000:N1} g", 156, 753, 11, Muted);
        Text($"{GetFPS()} fps", 924, 753, 11, Muted);
        if (thermal)
        {
            Text("-40", 648, 753, 10, Muted);
            for (int i = 0; i < 128; i++) DrawRectangle(678 + i, 756, 1, 4, HeatColor(-40 + i / 127.0 * 1540));
            Text("1500+ C", 814, 753, 10, Muted);
        }
        if (GetTime() < toastUntil) Text(toast, 16, 778, 11, Accent);
        else Text("LMB draw   /   RMB erase   /   Scroll brush size   /   Space pause", 16, 778, 11, Muted);
    }

    private void RenderPixels()
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            ref readonly var p = ref world.Cells[i];
            if (p.Element == Element.Empty)
            {
                pixels[i] = CanvasBackground;
                continue;
            }
            if (thermal) { pixels[i] = HeatColor(p.Temperature); continue; }
            Rgb rgb = Materials.Get(p.Element).Color;
            float variation = .86f + p.Variation / 255f * .24f;
            Color c = new((byte)Math.Clamp(rgb.R * variation, 0, 255),
                (byte)Math.Clamp(rgb.G * variation, 0, 255), (byte)Math.Clamp(rgb.B * variation, 0, 255), (byte)255);
            if (p.Element is Element.Fire or Element.Lava)
            {
                float flicker = (float)(.5 + .5 * Math.Sin(world.Tick * .22 + p.Variation));
                c = p.Element == Element.Fire
                    ? new((byte)255, (byte)(115 + 115 * flicker), (byte)(30 + 80 * flicker), (byte)255)
                    : new((byte)245, (byte)(70 + 65 * flicker), (byte)32, (byte)255);
            }
            else if (p.Temperature > 525)
            {
                float glow = (float)Math.Clamp((p.Temperature - 525) / 1100, 0, .85);
                c = Mix(c, new(255, 125, 54, 255), glow);
            }
            pixels[i] = c;
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
        DrawRectangle(0, 0, WindowWidth, WindowHeight, new(0, 0, 0, 185));
        DrawRectangleRounded(new(332, 157, 616, 487), .05f, 6, Panel);
        Text("YOUR PARTICLE LABORATORY", 367, 191, 24, Ink);
        Text("Every cell is a 2 x 2 pixel piece of matter.", 367, 229, 14, Accent);
        string[] lines = ["Left mouse: draw     Right mouse: erase     Middle mouse: pick",
            "Mouse wheel or [ / ]: change brush radius",
            "B: draw     H: heat     J: cool     1-9: quick material selection",
            "Space: pause     N: single step     T: temperature view",
            "Ctrl+S: save scene     Ctrl+O: load scene",
            "C: clear canvas     R: load demo     I: material properties",
            "F1 or Esc: close this guide"];
        for (int i = 0; i < lines.Length; i++) Text(lines[i], 367, 274 + i * 31, 13, Ink);
        Wrapped("Try heating water, melting ice, dropping sand into a liquid, or igniting wood. Hover over matter to inspect its mass and temperature.",
            367, 510, 540, 14, Muted);
        Text("A physics sandbox. See README for the model's limits.", 367, 603, 12, Accent);
    }

    private void ConfirmOverlay()
    {
        DrawRectangle(0, 0, WindowWidth, WindowHeight, new(0, 0, 0, 160));
        DrawRectangleRounded(new(414, 321, 452, 168), .08f, 6, Panel);
        Text(pendingAction == "demo" ? "Replace scene with the demo?" : "Clear the entire canvas?", 445, 345, 20, Ink);
        Text("Save your current scene first with Ctrl+S.", 445, 383, 13, Muted);
        Button(new(534, 425, 100, 36), "Confirm", true);
        Button(new(646, 425, 100, 36), "Cancel", false);
    }

    private void Property(string name, string value, int y)
    {
        Text(name, 1000, y, 11, Muted);
        float width = MeasureTextEx(font, value, 11, 0).X;
        Text(value, 1256 - width, y, 11, Ink);
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
            Press(Control.AirCooling);
            if (world.AmbientCooling) throw new InvalidOperationException("Air cooling toggle failed.");
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
    }
}
