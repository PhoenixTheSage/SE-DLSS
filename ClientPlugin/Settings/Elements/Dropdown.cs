using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClientPlugin.Dlss;
using Sandbox.Graphics.GUI;

namespace ClientPlugin.Settings.Elements;

[AttributeUsage(AttributeTargets.Property)]
internal class DropdownAttribute(
    int visibleRows = 20,
    string label = null,
    string description = null) : Attribute, IElement
{
    public readonly int VisibleRows = visibleRows;
    public readonly string Label = label;
    public readonly string Description = description;

    private static string UnCamelCase(string str)
    {
        return Regex.Replace(
            Regex.Replace(
                str,
                @"(\P{Ll})(\P{Ll}\p{Ll})",
                "$1 $2"
            ),
            @"(\p{Ll})(\P{Ll})",
            "$1 $2"
        );
    }

    public List<Control> GetControls(string name, Func<object> propertyGetter, Action<object> propertySetter)
    {
        var selectedEnum = propertyGetter();
        var choiceEnum = selectedEnum.GetType();

        var dropdown = new MyGuiControlCombobox(openAreaItemsCount: VisibleRows, toolTip: Description);
        var elements = Enum.GetNames(choiceEnum);

        for (var i = 0; i < elements.Length; i++)
        {
            if (choiceEnum == typeof(AntiAliasingChoice) &&
                (AntiAliasingChoice)i == AntiAliasingChoice.DLSS &&
                !GpuSupport.CanOfferDlss)
                continue;
            dropdown.AddItem(i, UnCamelCase(elements[i]));
        }

        if (choiceEnum == typeof(AntiAliasingChoice))
            GameAntiAliasing.BindPluginCombo(dropdown);
        else
        {
            void OnItemSelect()
            {
                var key = dropdown.GetSelectedKey();
                var value = elements[key];
                var enumValue = Enum.Parse(choiceEnum, value);
                propertySetter(enumValue);
            }

            dropdown.ItemSelected += OnItemSelect;
            dropdown.SelectItemByIndex(Convert.ToInt32(selectedEnum));
        }

        var label = Tools.Tools.GetLabelOrDefault(name, Label);
        return
        [
            new(new MyGuiControlLabel(text: label), minWidth: Control.LabelMinWidth),
            new(dropdown, fillFactor: 1f)
        ];
    }

    public List<Type> SupportedTypes { get; } =
    [
        typeof(Enum)
    ];
}
