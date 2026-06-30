// GlobalUsings.cs
// Resolves type ambiguity between WPF and Windows Forms namespaces.
// This project uses both UseWPF and UseWindowsForms, causing conflicts
// for types that exist in both frameworks.

// WPF Application type
global using WpfApplication = System.Windows.Application;

// WPF MessageBox
global using WpfMessageBox = System.Windows.MessageBox;

// WPF Clipboard
global using WpfClipboard = System.Windows.Clipboard;

// WPF Controls
global using WpfButton = System.Windows.Controls.Button;
global using WpfTextBox = System.Windows.Controls.TextBox;
global using WpfTextBlock = System.Windows.Controls.TextBlock;
global using WpfComboBox = System.Windows.Controls.ComboBox;
global using WpfCheckBox = System.Windows.Controls.CheckBox;

// WPF Media types
global using WpfColor = System.Windows.Media.Color;
global using WpfBrushes = System.Windows.Media.Brushes;
global using WpfFontFamily = System.Windows.Media.FontFamily;

// WPF Data types
global using WpfBinding = System.Windows.Data.Binding;

// WPF Input types
global using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

// WPF Shapes
global using WpfRectangle = System.Windows.Shapes.Rectangle;
