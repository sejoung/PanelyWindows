using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Panely.Core.Localization;

namespace Panely.App.Shell;

/// <summary>언어 전환 시 바인딩된 모든 문구를 즉시 갱신하는 소스.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource()
    {
        Loc.LanguageChanged += (_, _) =>
        {
            Version++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        };
    }

    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class LocKeyConverter : IValueConverter
{
    public static LocKeyConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => Loc.T(parameter as string ?? string.Empty);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>XAML: Text="{sh:Loc 'Open…'}" — 원본 영어 sourceKey로 현재 UI 언어 문구. 언어를 바꾸면 즉시 갱신.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(LocSource.Version))
        {
            Source = LocSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = LocKeyConverter.Instance,
            ConverterParameter = Key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
