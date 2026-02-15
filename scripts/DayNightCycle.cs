namespace ColonySim;

using Godot;

/// <summary>
/// Manages the full day/night cycle: sun arc, moon arc, sky color transitions,
/// ambient light changes. Attach as a child Node — it runs its own _Process.
///
/// Time convention: 0.0 = midnight, 0.25 = sunrise, 0.50 = noon, 0.75 = sunset.
///
/// The sun and moon are always 180° apart on the same orbital plane, doing a full
/// 360° rotation per cycle (Minecraft-style). The ProceduralSkyMaterial automatically
/// renders sun/moon disks based on DirectionalLight3D directions.
/// </summary>
public partial class DayNightCycle : Node
{
    /// <summary>Duration of one full day/night cycle in real seconds. Default 1200 = 20 minutes.</summary>
    [Export(PropertyHint.Range, "60,3600,10")]
    public float CycleDurationSeconds { get; set; } = 1200f;

    /// <summary>Starting time of day (0.0-1.0). Default 0.30 = morning.</summary>
    [Export(PropertyHint.Range, "0.0,1.0,0.01")]
    public float StartTime { get; set; } = 0.30f;

    /// <summary>Runtime speed multiplier. 1.0 = normal, 0.0 = paused.</summary>
    [Export(PropertyHint.Range, "0.0,10.0,0.1")]
    public float TimeScale { get; set; } = 1.0f;

    // Sun orbit direction (fixed azimuth — the compass direction the sun rises from)
    private const float SunAzimuth = 210f;
    private const float MaxSunElevation = 80f;  // not 90 to keep shadows visible at noon
    private const float MaxMoonElevation = 70f;  // slightly lower arc than sun

    // References passed from Main.SetupDynamicLighting()
    private DirectionalLight3D _sun;
    private DirectionalLight3D _moon;
    private ProceduralSkyMaterial _skyMaterial;
    private Environment _environment;

    // Time tracking
    private float _timeOfDay;

    // Keyboard debounce
    private bool _tWasPressed;
    private float _savedTimeScale;
    private bool _fastForwarding;
    private bool _rewinding;

    // ─── Keyframe Data ───────────────────────────────────────────────

    private struct TimeKeyframe
    {
        public float Time;
        public Color SkyTop;
        public Color SkyHorizon;
        public Color GroundHorizon;
        public Color SunColor;
        public float SunEnergy;
        public Color MoonColor;
        public float MoonEnergy;
        public Color AmbientColor;
        public float AmbientEnergy;
    }

    // Keyframes define the look at key moments. Values between keyframes are lerped.
    // Index 9 wraps to midnight (= index 0) for seamless interpolation.
    private static readonly TimeKeyframe[] Keyframes =
    {
        // 0: Midnight
        new()
        {
            Time = 0.00f,
            SkyTop = new Color(0.01f, 0.01f, 0.04f),
            SkyHorizon = new Color(0.02f, 0.02f, 0.05f),
            GroundHorizon = new Color(0.02f, 0.02f, 0.04f),
            SunColor = new Color(1f, 0.7f, 0.4f),
            SunEnergy = 0f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0.15f,
            AmbientColor = new Color(0.05f, 0.05f, 0.12f),
            AmbientEnergy = 0.05f,
        },
        // 1: Pre-dawn
        new()
        {
            Time = 0.20f,
            SkyTop = new Color(0.04f, 0.04f, 0.12f),
            SkyHorizon = new Color(0.15f, 0.05f, 0.08f),
            GroundHorizon = new Color(0.10f, 0.05f, 0.06f),
            SunColor = new Color(1f, 0.6f, 0.3f),
            SunEnergy = 0f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0.10f,
            AmbientColor = new Color(0.06f, 0.05f, 0.12f),
            AmbientEnergy = 0.06f,
        },
        // 2: Dawn (sunrise)
        new()
        {
            Time = 0.25f,
            SkyTop = new Color(0.25f, 0.25f, 0.55f),
            SkyHorizon = new Color(0.80f, 0.45f, 0.20f),
            GroundHorizon = new Color(0.55f, 0.35f, 0.20f),
            SunColor = new Color(1.0f, 0.70f, 0.35f),
            SunEnergy = 0.3f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0f,
            AmbientColor = new Color(0.35f, 0.30f, 0.25f),
            AmbientEnergy = 0.15f,
        },
        // 3: Morning
        new()
        {
            Time = 0.30f,
            SkyTop = new Color(0.30f, 0.50f, 0.80f),
            SkyHorizon = new Color(0.70f, 0.60f, 0.45f),
            GroundHorizon = new Color(0.55f, 0.50f, 0.40f),
            SunColor = new Color(1.0f, 0.90f, 0.75f),
            SunEnergy = 0.8f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0f,
            AmbientColor = new Color(0.55f, 0.50f, 0.45f),
            AmbientEnergy = 0.25f,
        },
        // 4: Midday (noon)
        new()
        {
            Time = 0.50f,
            SkyTop = new Color(0.35f, 0.55f, 0.85f),
            SkyHorizon = new Color(0.65f, 0.75f, 0.88f),
            GroundHorizon = new Color(0.65f, 0.65f, 0.60f),
            SunColor = new Color(1.0f, 0.98f, 0.92f),
            SunEnergy = 1.1f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0f,
            AmbientColor = new Color(0.70f, 0.70f, 0.68f),
            AmbientEnergy = 0.35f,
        },
        // 5: Afternoon
        new()
        {
            Time = 0.70f,
            SkyTop = new Color(0.32f, 0.50f, 0.82f),
            SkyHorizon = new Color(0.70f, 0.62f, 0.50f),
            GroundHorizon = new Color(0.58f, 0.52f, 0.42f),
            SunColor = new Color(1.0f, 0.92f, 0.80f),
            SunEnergy = 0.9f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0f,
            AmbientColor = new Color(0.60f, 0.55f, 0.48f),
            AmbientEnergy = 0.30f,
        },
        // 6: Sunset
        new()
        {
            Time = 0.75f,
            SkyTop = new Color(0.30f, 0.20f, 0.50f),
            SkyHorizon = new Color(0.85f, 0.35f, 0.15f),
            GroundHorizon = new Color(0.60f, 0.30f, 0.15f),
            SunColor = new Color(1.0f, 0.55f, 0.20f),
            SunEnergy = 0.4f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0f,
            AmbientColor = new Color(0.40f, 0.25f, 0.20f),
            AmbientEnergy = 0.15f,
        },
        // 7: Dusk
        new()
        {
            Time = 0.80f,
            SkyTop = new Color(0.06f, 0.06f, 0.18f),
            SkyHorizon = new Color(0.20f, 0.10f, 0.10f),
            GroundHorizon = new Color(0.12f, 0.08f, 0.08f),
            SunColor = new Color(1f, 0.5f, 0.2f),
            SunEnergy = 0f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0.08f,
            AmbientColor = new Color(0.08f, 0.06f, 0.14f),
            AmbientEnergy = 0.08f,
        },
        // 8: Night
        new()
        {
            Time = 0.90f,
            SkyTop = new Color(0.01f, 0.01f, 0.04f),
            SkyHorizon = new Color(0.02f, 0.02f, 0.05f),
            GroundHorizon = new Color(0.02f, 0.02f, 0.04f),
            SunColor = new Color(1f, 0.7f, 0.4f),
            SunEnergy = 0f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0.15f,
            AmbientColor = new Color(0.05f, 0.05f, 0.12f),
            AmbientEnergy = 0.05f,
        },
        // 9: Midnight wrap (identical to index 0 for seamless lerp)
        new()
        {
            Time = 1.00f,
            SkyTop = new Color(0.01f, 0.01f, 0.04f),
            SkyHorizon = new Color(0.02f, 0.02f, 0.05f),
            GroundHorizon = new Color(0.02f, 0.02f, 0.04f),
            SunColor = new Color(1f, 0.7f, 0.4f),
            SunEnergy = 0f,
            MoonColor = new Color(0.6f, 0.7f, 0.9f),
            MoonEnergy = 0.15f,
            AmbientColor = new Color(0.05f, 0.05f, 0.12f),
            AmbientEnergy = 0.05f,
        },
    };

    // ─── Initialization ──────────────────────────────────────────────

    /// <summary>
    /// Initialize with references to the lighting objects created by Main.SetupDynamicLighting().
    /// Must be called before the first _Process frame.
    /// </summary>
    public void Initialize(DirectionalLight3D sun, ProceduralSkyMaterial skyMaterial, Environment environment)
    {
        _sun = sun;
        _skyMaterial = skyMaterial;
        _environment = environment;

        // Create the moon as a child of this node
        _moon = new DirectionalLight3D();
        _moon.Name = "MoonLight";
        AddChild(_moon);

        // Moon setup: dim cool blue-white, visible disk in sky
        _moon.LightColor = new Color(0.6f, 0.7f, 0.9f);
        _moon.LightEnergy = 0.15f;
        _moon.ShadowEnabled = true;
        _moon.ShadowBias = 0.05f;
        _moon.ShadowNormalBias = 2.0f;
        _moon.ShadowOpacity = 0.3f; // faint moonlight shadows
        _moon.DirectionalShadowMaxDistance = 80f; // shorter range than sun
        _moon.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits; // cheaper
        // PCSS disabled (LightAngularDistance = 0) — Godot bug #86536
        _moon.SkyMode = DirectionalLight3D.SkyModeEnum.LightAndSky; // visible moon disk

        // Start at configured time
        _timeOfDay = StartTime;
        ApplyTime();

        GD.Print($"DayNightCycle: initialized at time {TimeState.FormattedTime}, cycle={CycleDurationSeconds}s");
    }

    // ─── Per-Frame Update ────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_sun == null) return; // not initialized yet

        // Advance time
        _timeOfDay += (float)(delta / CycleDurationSeconds) * TimeScale;

        // Wrap around midnight
        if (_timeOfDay >= 1.0f)
            _timeOfDay -= 1.0f;
        else if (_timeOfDay < 0.0f)
            _timeOfDay += 1.0f;

        // Keyboard controls
        ProcessTimeKeys();

        // Apply all interpolated values
        ApplyTime();

        // Update global state for other systems
        TimeState.TimeOfDay = _timeOfDay;
        TimeState.IsNight = _timeOfDay < 0.23f || _timeOfDay > 0.77f;

        int hours = (int)(_timeOfDay * 24f);
        int minutes = (int)((_timeOfDay * 24f - hours) * 60f);
        TimeState.FormattedTime = $"{hours:D2}:{minutes:D2}";
    }

    // ─── Core: Interpolate and Apply ─────────────────────────────────

    private void ApplyTime()
    {
        // Find the two keyframes surrounding the current time
        int lo = 0;
        for (int i = 0; i < Keyframes.Length - 1; i++)
        {
            if (Keyframes[i + 1].Time > _timeOfDay)
            {
                lo = i;
                break;
            }
        }
        int hi = lo + 1;

        // Lerp factor between the two keyframes
        float range = Keyframes[hi].Time - Keyframes[lo].Time;
        float t = range > 0.0001f ? (_timeOfDay - Keyframes[lo].Time) / range : 0f;

        ref readonly var a = ref Keyframes[lo];
        ref readonly var b = ref Keyframes[hi];

        // --- Sky material ---
        _skyMaterial.SkyTopColor = a.SkyTop.Lerp(b.SkyTop, t);
        _skyMaterial.SkyHorizonColor = a.SkyHorizon.Lerp(b.SkyHorizon, t);
        _skyMaterial.GroundHorizonColor = a.GroundHorizon.Lerp(b.GroundHorizon, t);

        // Match ground bottom to a darker version of ground horizon
        var gh = a.GroundHorizon.Lerp(b.GroundHorizon, t);
        _skyMaterial.GroundBottomColor = new Color(gh.R * 0.3f, gh.G * 0.3f, gh.B * 0.3f);

        // --- Sun ---
        float sunEnergy = Mathf.Lerp(a.SunEnergy, b.SunEnergy, t);
        _sun.LightColor = a.SunColor.Lerp(b.SunColor, t);
        _sun.LightEnergy = sunEnergy;
        _sun.Visible = sunEnergy > 0.01f; // hide when energy is ~0 to prevent glow below horizon

        // --- Moon ---
        float moonEnergy = Mathf.Lerp(a.MoonEnergy, b.MoonEnergy, t);
        _moon.LightColor = a.MoonColor.Lerp(b.MoonColor, t);
        _moon.LightEnergy = moonEnergy;
        _moon.Visible = moonEnergy > 0.01f;

        // --- Ambient ---
        _environment.AmbientLightColor = a.AmbientColor.Lerp(b.AmbientColor, t);
        _environment.AmbientLightEnergy = Mathf.Lerp(a.AmbientEnergy, b.AmbientEnergy, t);

        // --- Celestial body rotation ---
        UpdateCelestialBodies();

        // --- Background mode: respect SliceState ---
        if (!SliceState.Enabled)
        {
            _environment.BackgroundMode = Environment.BGMode.Sky;
        }
        // When SliceState.Enabled, CameraController/FreeFlyCamera own the background mode
    }

    // ─── Sun/Moon Rotation ───────────────────────────────────────────

    /// <summary>
    /// Rotate the sun and moon based on current time of day.
    /// Sun: rises at time 0.25 (east), peaks at 0.50 (overhead), sets at 0.75 (west).
    /// Moon: always 180° opposite the sun.
    /// </summary>
    private void UpdateCelestialBodies()
    {
        // Sun angle: maps time to 0-360° where 0.25=0° (horizon), 0.50=90° (peak)
        float sunDegrees = (_timeOfDay - 0.25f) * 360f;
        float sunElevation = Mathf.Sin(Mathf.DegToRad(sunDegrees)) * MaxSunElevation;
        SetLightAngle(_sun, sunElevation, SunAzimuth);

        // Moon: 180° offset from sun, slightly lower arc
        float moonDegrees = sunDegrees + 180f;
        float moonElevation = Mathf.Sin(Mathf.DegToRad(moonDegrees)) * MaxMoonElevation;
        SetLightAngle(_moon, moonElevation, SunAzimuth + 180f);
    }

    /// <summary>
    /// Set a DirectionalLight3D's direction from elevation (degrees above horizon) and azimuth.
    /// Same logic as Main.SetSunAngle — extracted to work with any light.
    /// </summary>
    private static void SetLightAngle(DirectionalLight3D light, float elevationDeg, float azimuthDeg)
    {
        if (light == null) return;

        // Start facing down -Z (default Godot forward), then rotate:
        // 1. Pitch down by elevation (rotate around X)
        // 2. Rotate around Y by azimuth
        var basis = Basis.Identity;
        basis = basis.Rotated(Vector3.Right, -Mathf.DegToRad(elevationDeg));
        basis = basis.Rotated(Vector3.Up, Mathf.DegToRad(azimuthDeg));
        light.Basis = basis;
    }

    // ─── Keyboard Controls ───────────────────────────────────────────

    private void ProcessTimeKeys()
    {
        // T: print current time (debounced)
        bool tNow = Input.IsKeyPressed(Key.T);
        if (tNow && !_tWasPressed)
        {
            GD.Print($"Time: {TimeState.FormattedTime} (raw={_timeOfDay:F3}, night={TimeState.IsNight})");
        }
        _tWasPressed = tNow;

        // ]: fast forward 20x while held
        bool fastNow = Input.IsKeyPressed(Key.Bracketright);
        if (fastNow && !_fastForwarding)
        {
            _savedTimeScale = TimeScale;
            TimeScale = 20f;
            _fastForwarding = true;
            GD.Print("Time: FAST FORWARD (20x) — hold ] to continue");
        }
        else if (!fastNow && _fastForwarding)
        {
            TimeScale = _savedTimeScale;
            _fastForwarding = false;
            GD.Print($"Time: normal speed ({TimeScale}x) at {TimeState.FormattedTime}");
        }

        // [: rewind 20x while held
        bool revNow = Input.IsKeyPressed(Key.Bracketleft);
        if (revNow && !_rewinding)
        {
            _savedTimeScale = TimeScale;
            TimeScale = -20f;
            _rewinding = true;
            GD.Print("Time: REWIND (20x) — hold [ to continue");
        }
        else if (!revNow && _rewinding)
        {
            TimeScale = _savedTimeScale;
            _rewinding = false;
            GD.Print($"Time: normal speed ({TimeScale}x) at {TimeState.FormattedTime}");
        }
    }
}
