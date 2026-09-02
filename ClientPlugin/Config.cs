using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Serialization;
using ClientPlugin.Dlss;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private AntiAliasingChoice antiAliasing = AntiAliasingChoice.Off;
    private DlssMode mode = DlssMode.Quality;
    private DlssModel model = DlssModel.LatestModel;
    private float sharpness = 0.5f;
    private bool useAnomalyMotionVectors = true;

    #endregion

    #region User interface

    public readonly string Title = "DLSS";

    internal static bool SuppressApply;

    [Separator("Anti-aliasing")]

    [Dropdown(visibleRows: 10, label: "Anti-aliasing",
        description: "DLSS replaces FXAA; choose Off or FXAA to use the game's anti-aliasing.")]
    public AntiAliasingChoice AntiAliasing
    {
        get => antiAliasing;
        set
        {
            if (value == AntiAliasingChoice.DLSS && GpuSupport.Probed && !GpuSupport.CanOfferDlss)
                value = AntiAliasingChoice.Off;
            SetField(ref antiAliasing, value);
        }
    }

    [XmlIgnore]
    public bool Enabled => antiAliasing == AntiAliasingChoice.DLSS;

    // Old configs stored <Enabled>true</Enabled>. XmlSerializer still calls this setter.
    [XmlElement("Enabled")]
    [Browsable(false)]
    public bool EnabledCompat
    {
        get => Enabled;
        set
        {
            if (value)
                AntiAliasing = AntiAliasingChoice.DLSS;
            else if (antiAliasing == AntiAliasingChoice.DLSS)
                AntiAliasing = AntiAliasingChoice.Off;
        }
    }

    [Separator("DLSS Super Resolution")]

    [Dropdown(
        description: "Quality trades internal resolution against image quality. DLAA stays at native resolution.")]
    public DlssMode Mode
    {
        get => mode;
        set => SetField(ref mode, value);
    }

    [Dropdown(description: "DLSS model. Latest Model uses transformer K; CNN F is the legacy option. " +
                           "NVIDIA App overrides do not apply to this unofficial title.")]
    public DlssModel Model
    {
        get => model;
        set => SetField(ref model, value);
    }

    [Slider(0f, 1f, 0.05f, label: "Sharpness",
        description: "Optional sharpening; transformer models may ignore it.")]
    public float Sharpness
    {
        get => sharpness;
        set => SetField(ref sharpness, value);
    }

    [Separator("Motion vectors")]

    [Checkbox(label: "Use Anomaly Framework",
        description: "Use Anomaly's object-aware motion vectors when available. " +
                     "Disable to use camera-from-depth motion vectors. " +
                     "Reactive mask and AfterUpscale notify stay on when Anomaly is loaded.")]
    public bool UseAnomalyMotionVectors
    {
        get => useAnomalyMotionVectors;
        set => SetField(ref useAnomalyMotionVectors, value);
    }

    [Separator("Status")]

    [Button(label: "Show Status", description: "GPU, NGX support, resolutions, and Anomaly buffer status")]
    // ReSharper disable once UnusedMember.Global
    public static void ShowStatus()
    {
        GpuSupport.TryProbe();
        MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
            MyMessageBoxStyleEnum.Info,
            buttonType: MyMessageBoxButtonsType.OK,
            messageText: new StringBuilder(DlssStatus.CurrentText),
            messageCaption: new StringBuilder("DLSS Status"),
            size: new Vector2(0.7f, 0.65f),
            moveTextUp: false
        ));
    }

    #endregion

    #region Property change notification boilerplate

    // Property notifications can run while Current is still being initialized.
    public static readonly Config Default = new();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(AntiAliasing) || propertyName == nameof(Mode) || propertyName == nameof(Model) ||
            propertyName == nameof(Sharpness) || propertyName == nameof(UseAnomalyMotionVectors))
        {
            DebugLog.Write("config " + propertyName + " aa=" + antiAliasing + " mode=" + mode +
                           " model=" + model + " sharpness=" + sharpness +
                           " anomalyMv=" + useAnomalyMotionVectors);
            DlssRuntime.NotifyConfigChanged();
        }
        if (propertyName == nameof(AntiAliasing) && !SuppressApply)
            GameAntiAliasing.ApplyFromConfig();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    #endregion
}
