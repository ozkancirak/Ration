using Ration.Core.Cost;
using Ration.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ration.App.Views;

/// <summary>All provider tabs share the same most-used-model line.</summary>
public static class ProviderModelLine
{
    public static void SetReport(TextBlock line, CostReport? report)
    {
        var models = Summarize(report);
        var total = models.Sum(model => model.Tokens);
        if (models.Count == 0 || total <= 0)
        {
            line.Text = string.Empty;
            line.Visibility = Visibility.Collapsed;
            return;
        }

        var top = models[0];
        line.Text = $"En çok: {top.Model} · %{top.Tokens * 100d / total:F0}";
        line.Visibility = Visibility.Visible;
    }

    public static IReadOnlyList<ModelTokenUsage> Summarize(CostReport? report)
    {
        if (report?.Models is not { } source) return Array.Empty<ModelTokenUsage>();

        return source
            .Where(model => model.Tokens > 0)
            .GroupBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelTokenUsage(
                group.Key,
                group.Sum(model => model.Tokens),
                group.Sum(model => model.InputTokens),
                group.Sum(model => model.OutputTokens),
                group.Sum(model => model.CacheReadTokens),
                group.Sum(model => model.CacheCreationTokens)))
            .OrderByDescending(model => model.Tokens)
            .ToList();
    }
}
