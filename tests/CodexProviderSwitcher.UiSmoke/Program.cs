using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CodexProviderSwitcher;
using CodexProviderSwitcher.Core;

internal static class Program
{
    // Instantiate WPF resources/controls without displaying windows, starting
    // MainWindow.Loaded, interacting with the user's apps, or reading their keys.
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var main = new MainWindow();
            var editButton = Require<Button>(main, "EditProviderProfileButton");
            var deleteButton = Require<Button>(main, "DeleteProviderProfileButton");
            Check(editButton.Style is not null && deleteButton.Style is not null,
                "The account controls did not load their WPF styles.");
            var accountActions = (WrapPanel)editButton.Parent;
            var accountGrid = (Grid)accountActions.Parent;
            Check(accountGrid.RowDefinitions.Count > Grid.GetRow(accountActions),
                "Account actions overlap the saved-account picker.");
            main.Close();

            foreach (var language in new[] { Localizer.ChineseCode, Localizer.EnglishCode })
            {
                Localizer.Use(language);
                var profile = new ProviderProfile
                {
                    DisplayName = "Account fixture", BaseUrl = "https://service.example/v1",
                    Model = "test-model"
                };
                var editor = new ProviderProfileEditorWindow(profile);
                var save = Require<Button>(editor, "SaveButton");
                var cancel = Require<Button>(editor, "CancelButton");
                var key = Require<PasswordBox>(editor, "NewApiKeyPasswordBox");
                var baseUrl = Require<TextBox>(editor, "BaseUrlTextBox");
                Check(save.IsDefault && cancel.IsCancel, "The editor lost keyboard save/cancel behavior.");
                Check(key.Password.Length == 0 && editor.Edit is null,
                    "The editor exposed a saved key or created an edit before confirmation.");
                Check(editor.Title == (language == Localizer.EnglishCode ? "Edit account" : "编辑账号"),
                    "The editor title is not bilingual.");
                baseUrl.Text = "https://different.example/v1";
                typeof(ProviderProfileEditorWindow).GetMethod("SaveButton_Click",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [save, new RoutedEventArgs()]);
                Check(editor.Edit is null && Require<TextBlock>(editor, "ValidationErrorText").Visibility == Visibility.Visible,
                    "Changing an endpoint without a new key did not remain an invalid draft.");
                Check(profile.BaseUrl == "https://service.example/v1" && profile.Model == "test-model",
                    "Opening/validating an editor mutated its original account.");
                editor.Close();
            }
            app.Shutdown();
            Console.WriteLine("All non-visual WPF account smoke tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static T Require<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing WPF control: {name}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
