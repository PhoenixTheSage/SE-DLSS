using ClientPlugin.Dlss;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using Sandbox.Graphics.GUI;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Serialization;
using VRageMath;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    #region Options

    private AntiAliasingChoice antiAliasing = AntiAliasingChoice.Off;
    private DlssMode mode = DlssMode.Quality;
    private DlssModel model = DlssModel.LatestModel;
    private float sharpness = 0.5f;

    #endregion

    #region User interface

    public readonly string Title = "DLSS";

    internal static bool SuppressApply;

    [Separator("Anti-aliasing")]

    [Dropdown(visibleRows: 10, label: "Anti-aliasing", description: "DLSS replaces FXAA. Pick Off or FXAA to use the game's anti-aliasing instead.")]
    public AntiAliasingChoice AntiAliasing
    {
        get => antiAliasing;
        set => SetField(ref antiAliasing, value);
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

    [Dropdown(description: "Quality trades internal resolution against image quality. DLAA stays at native resolution.")]
    public DlssMode Mode
    {
        get => mode;
        set => SetField(ref mode, value);
    }

    [Dropdown(description: "Transformer model used for Super Resolution. NVIDIA App cannot override this unofficial title. Latest Model uses transformer K at every quality level.")]
    public DlssModel Model
    {
        get => model;
        set => SetField(ref model, value);
    }

    [Slider(0f, 1f, 0.05f, SliderAttribute.SliderType.Float, label: "Sharpness", description: "Optional sharpening. Transformer models may ignore this.")]
    public float Sharpness
    {
        get => sharpness;
        set => SetField(ref sharpness, value);
    }

    [Separator("Status")]

    [Button(label: "Show Status", description: "GPU, NGX, and current internal resolution")]
    public void ShowStatus()
    {
        MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
            MyMessageBoxStyleEnum.Info,
            buttonType: MyMessageBoxButtonsType.OK,
            messageText: new StringBuilder(DlssStatus.CurrentText),
            messageCaption: new StringBuilder("DLSS Status"),
            size: new Vector2(0.6f, 0.5f)
        ));
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(AntiAliasing) || propertyName == nameof(Mode) || propertyName == nameof(Model) ||
            propertyName == nameof(Sharpness))
        {
            DebugLog.Write("config " + propertyName + " aa=" + antiAliasing + " mode=" + mode +
                           " model=" + model + " sharpness=" + sharpness);
            DlssRuntime.NotifyConfigChanged();
        }
        if (propertyName == nameof(AntiAliasing) && !SuppressApply)
            GameAntiAliasing.ApplyFromConfig();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
