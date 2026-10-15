using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace Powder;

public sealed class SandboxApp
{
    public const int GridWidth = 480, GridHeight = 336;
    private const int WindowWidth = 1280, WindowHeight = 800, CanvasX = 16, CanvasY = 96;
    private static readonly Color Background = new(12, 17, 25, 255);
    private static readonly Color Panel = new(21, 29, 41, 255);
    private static readonly Color Border = new(43, 55, 72, 255);
    private static readonly Color Ink = new(224, 232, 241, 255);
    private static readonly Color Muted = new(135, 151, 172, 255);
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
    private bool paused, thermal, help, showLabels = true, painting;
    private int radius = 5, speed = 1, tool; // 0: matter, 1: heat, 2: cool
    private Vector2 previous;
    private string toast = "Welcome to your particle laboratory.";
    private double toastUntil = 5;
    private double accumulator;
    private double stepMilliseconds;
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
            Scenes.Demo(world);
            stats = world.Statistics();
            int frame = 0;
            while (!WindowShouldClose())
            {
                Input();
                if (smoke) SmokeActions(frame);
                double dt = Math.Min(GetFrameTime(), .1);
                accumulator = paused ? 0 : Math.Min(accumulator + dt * speed, World.FixedDelta * 8);
                var watch = Stopwatch.StartNew();
                while (accumulator >= World.FixedDelta)
                {
                    world.Step(); accumulator -= World.FixedDelta;
                }
                stepMilliseconds = watch.Elapsed.TotalMilliseconds;
                if (frame % 15 == 0) stats = world.Statistics();
                RenderPixels();
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
                        Console.WriteLine($"UI SMOKE PASS: {frame} frames; {world.Count} particles; palette, painting, heat, pause, thermal view, save/load.");
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
        if (IsKeyPressed(KeyboardKey.G)) showLabels = !showLabels;
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

    private void Click(Vector2 p)
    {
        bool Hit(int x, int y, int w, int h) => CheckCollisionPointRec(p, new(x, y, w, h));
        if (Hit(16, 62, 94, 25)) paused = !paused;
        if (Hit(118, 62, 74, 25)) { paused = true; world.Step(); }
        if (Hit(200, 62, 83, 25)) pendingAction = "clear";
        if (Hit(291, 62, 95, 25)) pendingAction = "demo";
        if (Hit(395, 62, 76, 25)) speed = speed == 1 ? 2 : speed == 2 ? 4 : 1;
        if (Hit(754, 62, 107, 25)) thermal = false;
        if (Hit(869, 62, 107, 25)) thermal = true;
        if (Hit(1199, 20, 65, 27)) help = !help;
        for (int i = 0; i < 15; i++)
            if (Hit(1006 + (i % 3) * 80, 145 + (i / 3) * 47, 74, 40))
            { selected = (Element)(i == 14 ? 0 : i + 1); tool = 0; }
        if (Hit(1006, 606, 74, 31)) tool = 0;
        if (Hit(1086, 606, 74, 31)) tool = 1;
        if (Hit(1166, 606, 74, 31)) tool = 2;
        if (Hit(1006, 651, 30, 26)) radius = Math.Max(1, radius - 1);
        if (Hit(1214, 651, 30, 26)) radius = Math.Min(30, radius + 1);
        if (Hit(1006, 694, 115, 29)) Save();
        if (Hit(1129, 694, 115, 29)) Load();
        if (Hit(1006, 738, 238, 22))
        {
            world.AmbientCooling = !world.AmbientCooling;
            Notify(world.AmbientCooling ? "Air cooling enabled: ambient temperature 20 C." : "Air cooling disabled. Neighbor heat exchange stays active.");
        }
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
        DrawRectangle(19, 20, 10, 10, Accent);
        DrawRectangle(32, 20, 10, 10, new(78, 151, 151, 255));
        DrawRectangle(19, 33, 10, 10, new(78, 151, 151, 255));
        DrawRectangle(32, 33, 10, 10, Accent);
        Text("POWDER LAB", 54, 15, 24, Ink);
        Text("MATTER IN MOTION", 247, 25, 11, Muted);
        Pill(1026, 23, "EARLY ACCESS  /  0.1", Accent);
        Button(new(1199, 20, 65, 27), "?  Help", false);
        Button(new(16, 62, 94, 25), paused ? ">  Resume" : "II  Pause", paused);
        Button(new(118, 62, 74, 25), "Step  >", false);
        Button(new(200, 62, 83, 25), "Clear", false);
        Button(new(291, 62, 95, 25), "Load demo", false);
        Button(new(395, 62, 76, 25), $"{speed}x speed", speed != 1);
        Text(paused ? "SIMULATION PAUSED" : "LIVE SIMULATION", 493, 69, 10, paused ? Muted : Accent);
        Button(new(754, 62, 107, 25), "Material", !thermal);
        Button(new(869, 62, 107, 25), "Temperature", thermal);
        Text("MATERIALS", 1006, 111, 13, Ink);
        Text("14 elements", 1158, 113, 10, Muted);
    }

    private void Canvas()
    {
        DrawTextureEx(texture, new(CanvasX, CanvasY), 0, 2, Color.White);
        DrawRectangleLines(CanvasX - 1, CanvasY - 1, GridWidth * 2 + 2, GridHeight * 2 + 2, Border);
        if (showLabels)
        {
            SceneLabel(39, "01", "DENSITY & GRAVITY", "sand / oil / water");
            SceneLabel(368, "02", "THERMAL BRIDGE", "copper / lava / water");
            SceneLabel(704, "03", "COMBUSTION", "wood / fire / smoke");
        }
        if (thermal)
        {
            DrawRectangle(35, 699, 250, 50, new(12, 17, 25, 230));
            Text("TEMPERATURE  /  C", 46, 706, 10, Ink);
            for (int i = 0; i < 220; i++) DrawRectangle(46 + i, 725, 1, 5, HeatColor(-40 + i / 219.0 * 1540));
            Text("-40", 46, 735, 9, Muted); Text("100", 125, 735, 9, Muted); Text("1500+", 231, 735, 9, Muted);
        }
        if (paused) Pill(847, 111, "PAUSED", Accent);
        Vector2 m = GetMousePosition();
        if (InCanvas(m) && !help && pendingAction is null)
        {
            int x = (int)(m.X - CanvasX) / 2, y = (int)(m.Y - CanvasY) / 2;
            DrawCircleLines(CanvasX + x * 2 + 1, CanvasY + y * 2 + 1, radius * 2, new(230, 240, 250, 160));
            var p = world.At(x, y);
            string detail = p.Element == Element.Empty ? $"EMPTY   [{x}, {y}]"
                : $"{Materials.Get(p.Element).Name.ToUpperInvariant()}   {p.Temperature:F1} C   {p.Mass * 1000:F3} g   [{x}, {y}]";
            float width = MeasureTextEx(font, detail, 12, 0).X + 24;
            float tx = Math.Clamp(m.X + 18, CanvasX + 4, CanvasX + GridWidth * 2 - width - 4);
            float ty = Math.Clamp(m.Y + 20, CanvasY + 4, CanvasY + GridHeight * 2 - 32);
            DrawRectangleRounded(new(tx, ty, width, 28), .2f, 4, new(29, 41, 54, 245));
            Text(detail, tx + 12, ty + 7, 12, Ink);
        }
    }

    private void Sidebar()
    {
        DrawRectangleRounded(new(992, 135, 272, 252), .04f, 4, Panel);
        for (int i = 0; i < 15; i++)
        {
            Element e = (Element)(i == 14 ? 0 : i + 1);
            var mat = Materials.Get(e);
            int x = 1006 + i % 3 * 80, y = 145 + i / 3 * 47;
            bool active = selected == e && tool == 0;
            Color c = new(mat.Color.R, mat.Color.G, mat.Color.B, (byte)255);
            DrawRectangleRounded(new(x, y, 74, 40), .15f, 4,
                active ? new(42, 65, 71, 255) : Hover(new(x, y, 74, 40)) ? new(42, 52, 67, 255) : new(30, 40, 54, 255));
            if (active) DrawRectangleRoundedLinesEx(new(x, y, 74, 40), .15f, 4, 1, Accent);
            DrawRectangle(x + 8, y + 9, 7, 7, c);
            Text(mat.Name, x + 8, y + 23, 10, active ? Accent : Ink);
            if (i < 9) Text((i + 1).ToString(), x + 58, y + 8, 9, Muted);
        }
        var selectedMaterial = Materials.Get(selected);
        DrawRectangleRounded(new(992, 401, 272, 173), .05f, 4, Panel);
        Text("MATERIAL PROPERTIES", 1006, 415, 10, Muted);
        var swatch = selectedMaterial.Color;
        DrawRectangle(1006, 441, 12, 12, new(swatch.R, swatch.G, swatch.B, (byte)255));
        Text(selectedMaterial.Name, 1026, 436, 20, Ink);
        Text(selectedMaterial.State.ToString().ToUpperInvariant(), 1171, 443, 9, Accent);
        Property("Density", $"{selectedMaterial.Density:N0} kg/m3", 470);
        Property("Heat capacity", $"{selectedMaterial.SpecificHeat:N0} J/kg K", 492);
        Property("Conductivity", $"{selectedMaterial.Conductivity:0.###} W/m K", 514);
        Wrapped(selectedMaterial.Description, 1006, 542, 242, 10, Muted);
        Text("INSTRUMENTS", 1006, 589, 11, Muted);
        Button(new(1006, 606, 74, 31), "B  Draw", tool == 0);
        Button(new(1086, 606, 74, 31), "H  Heat", tool == 1);
        Button(new(1166, 606, 74, 31), "J  Cool", tool == 2);
        Button(new(1006, 651, 30, 26), "-", false);
        Text($"Brush  {radius * 2 + 1} cells", 1051, 658, 12, Ink);
        Button(new(1214, 651, 30, 26), "+", false);
        Button(new(1006, 694, 115, 29), "Save  Ctrl+S", false);
        Button(new(1129, 694, 115, 29), "Load  Ctrl+O", false);
        DrawRectangle(1006, 742, 12, 12, world.AmbientCooling ? Accent : Border);
        Text("Ambient cooling  /  20 C", 1026, 741, 11, Muted);
    }

    private void Footer()
    {
        Text($"{stats.Particles:N0} particles", 16, 779, 11, Ink);
        Text($"{stats.Mass * 1000:N1} g", 156, 779, 11, Muted);
        Text($"{GetFPS()} fps", 272, 779, 11, Muted);
        Text($"physics {stepMilliseconds:F1} ms", 336, 779, 11, Muted);
        Text("2 x 2 px", 462, 779, 11, Accent);
        if (GetTime() < toastUntil) Text(toast, 560, 779, 10, Accent);
        else Text("LMB draw   RMB erase   MMB pick   Wheel size   Space pause   F1 help", 560, 779, 10, Muted);
    }

    private void RenderPixels()
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            ref readonly var p = ref world.Cells[i];
            if (p.Element == Element.Empty)
            {
                pixels[i] = i % GridWidth % 16 == 0 && i / GridWidth % 16 == 0
                    ? new(29, 39, 52, 255) : new(13, 20, 30, 255);
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

    private void SceneLabel(int x, string n, string title, string subtitle)
    {
        Text(n, x, 120, 10, Accent); Text(title, x + 24, 119, 12, Muted);
        Text(subtitle, x + 24, 138, 10, new(87, 105, 126, 255));
    }

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
            "C: clear canvas     R: demo scene     G: hide scene labels",
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
        Text(name, 1006, y, 11, Muted);
        float width = MeasureTextEx(font, value, 11, 0).X;
        Text(value, 1244 - width, y, 11, Ink);
    }

    private void Button(Rectangle r, string label, bool active)
    {
        DrawRectangleRounded(r, .18f, 4, active ? new(39, 69, 69, 255)
            : Hover(r) ? new(45, 57, 74, 255) : new(28, 38, 52, 255));
        if (active) DrawRectangleRoundedLinesEx(r, .18f, 4, 1, new(67, 126, 118, 255));
        Vector2 size = MeasureTextEx(font, label, 11, 0);
        Text(label, r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2, 11, active ? Accent : Ink);
    }

    private void Pill(int x, int y, string text, Color c)
    {
        Vector2 size = MeasureTextEx(font, text, 9, 0);
        DrawRectangleRounded(new(x, y, size.X + 18, 21), .2f, 4, new(26, 46, 46, 255));
        Text(text, x + 9, y + 6, 9, c);
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
        if (frame == 5) { Click(new(1095, 155)); Paint(110, 30); }
        if (frame == 10) { Click(new(1095, 615)); Paint(110, 30); }
        if (frame == 15) { Click(new(1015, 615)); Paint(140, 30); }
        if (frame == 20) Click(new(900, 72));
        if (frame == 30)
        {
            Click(new(1030, 705)); smokeSavedMass = world.Statistics().Mass;
        }
        if (frame == 35)
        {
            Paint(130, 30, true);
            Click(new(240, 72)); ConfirmAction();
            Click(new(1150, 705));
            if (Math.Abs(world.Statistics().Mass - smokeSavedMass) > 1e-12)
                throw new InvalidOperationException("UI failed to restore the saved scene.");
        }
        if (frame == 50) Click(new(60, 72));
    }
}
