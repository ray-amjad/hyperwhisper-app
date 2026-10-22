namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// A count with the right English noun form for status lines ("1 mode", "2 modes", "0 modes"),
/// instead of the developer shorthand with a bracketed "s" (#630). Status lines are plain English today;
/// this does not localize.
/// </summary>
public static class Plural
{
    public static string Count(int n, string singular, string plural) => $"{n} {(n == 1 ? singular : plural)}";
}
