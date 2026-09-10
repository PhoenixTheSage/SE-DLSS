using System.Collections.Generic;
using System.Text;
using Sandbox;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Settings;

/// <summary>
/// Scrollable status panel with insets so the body stays below the caption
/// and above the OK button. Keen's message box draws text through both.
/// </summary>
internal sealed class StatusScreen : MyGuiScreenBase
{
    const float TextScale = 0.62f;
    const float TopInset = 0.115f;
    const float BottomInset = 0.125f;
    const string TextFont = "Blue";

    static readonly StringBuilder MeasureBuffer = new(256);

    readonly string bodyText;

    public StatusScreen(string text)
        : base(
            new Vector2(0.5f, 0.5f),
            MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            new Vector2(0.70f, 0.74f),
            false,
            null,
            MySandboxGame.Config.UIBkOpacity,
            MySandboxGame.Config.UIOpacity)
    {
        bodyText = text ?? "";
        EnabledBackgroundFade = true;
        m_closeOnEsc = true;
        m_drawEvenWithoutFocus = true;
        CanHideOthers = true;
        CanBeHidden = true;
        CloseButtonEnabled = true;
    }

    public override string GetFriendlyName() => "DlssStatus";

    public override void LoadContent()
    {
        base.LoadContent();
        RecreateControls(true);
    }

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("DLSS Status");

        var screenSize = Size ?? new Vector2(0.70f, 0.74f);
        var textSize = new Vector2(screenSize.X - 0.08f, screenSize.Y - TopInset - BottomInset);
        var wrapWidth = textSize.X - 0.05f;
        string wrapped;
        try
        {
            wrapped = WrapText(bodyText, wrapWidth);
        }
        catch
        {
            wrapped = bodyText;
        }

        Controls.Add(new MyGuiControlMultilineText(
            position: new Vector2(0f, -screenSize.Y * 0.5f + TopInset),
            size: textSize,
            backgroundColor: null,
            font: TextFont,
            textScale: TextScale,
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: new StringBuilder(wrapped),
            drawScrollbarV: true,
            drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP,
        });

        Controls.Add(new MyGuiControlButton(
            text: new StringBuilder("OK"),
            onButtonClick: _ => CloseScreen())
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_BOTTOM,
            Position = new Vector2(0f, screenSize.Y * 0.5f - 0.035f),
        });
    }

    static string WrapText(string description, float maxWidth)
    {
        if (string.IsNullOrEmpty(description))
            return description;

        var sb = new StringBuilder();
        var first = true;
        foreach (var line in WrapLines(description, maxWidth))
        {
            if (!first)
                sb.Append('\n');
            first = false;
            sb.Append(line);
        }

        return sb.ToString();
    }

    static IEnumerable<string> WrapLines(string description, float maxWidth)
    {
        foreach (var paragraph in description.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (word.Length == 0)
                    continue;

                if (Measure(word) > maxWidth)
                {
                    if (line.Length > 0)
                    {
                        yield return line.ToString();
                        line.Clear();
                    }

                    foreach (var chunk in BreakWord(word, maxWidth))
                        yield return chunk;
                    continue;
                }

                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate) > maxWidth)
                {
                    yield return line.ToString();
                    line.Clear();
                    line.Append(word);
                }
                else
                {
                    if (line.Length > 0)
                        line.Append(' ');
                    line.Append(word);
                }
            }

            if (line.Length > 0)
                yield return line.ToString();
        }
    }

    static IEnumerable<string> BreakWord(string word, float maxWidth)
    {
        var chunk = new StringBuilder();
        foreach (var ch in word)
        {
            chunk.Append(ch);
            if (Measure(chunk.ToString()) > maxWidth && chunk.Length > 1)
            {
                chunk.Length--;
                yield return chunk.ToString();
                chunk.Clear();
                chunk.Append(ch);
            }
        }

        if (chunk.Length > 0)
            yield return chunk.ToString();
    }

    static float Measure(string text)
    {
        MeasureBuffer.Clear();
        MeasureBuffer.Append(text);
        return MyGuiManager.MeasureString(TextFont, MeasureBuffer, TextScale).X;
    }
}
