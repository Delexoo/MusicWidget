using System.Windows;
using System.Windows.Input;

namespace MusicWidget.Views;

public partial class NameInputWindow : Window
{
    public string? Value { get; private set; }
    public bool Accepted { get; private set; }

    public NameInputWindow(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        TitleText.Text = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = InputBox.Text?.Trim();
        Accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
