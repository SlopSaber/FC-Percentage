using CountersPlus.Custom;
using FCPercentage.FCPCore.Configuration;
using FCPercentage.FCPCounter.Configuration;
using System;
using TMPro;
using UnityEngine;

namespace FCPercentage.FCPCounter
{
    public class FCPercentageCounterPreview : ICounterPreview
    {
        public void Render(CounterPreviewContext preview)
        {
            CounterSettings config = PluginConfig.Instance.CounterSettings;
            if (config.EnableLabel == CounterLabelOptions.AboveCounter)
            {
                TMP_Text label = preview.CreateText(new Vector3(0, config.Advanced.LabelAboveCounterTextOffset + config.Advanced.CounterOffset, 0));
                label.text = config.Advanced.LabelAboveCounterText;
                label.fontSize *= config.Advanced.LabelAboveCounterTextSize;
            }

            TMP_Text percentage = preview.CreateText(new Vector3(0, config.Advanced.CounterOffset, 0));
            percentage.fontSize *= config.Advanced.PercentageSize;
            percentage.lineSpacing = config.Advanced.PercentageTotalAndSplitLineHeight * 100;

            string text = config.EnableLabel == CounterLabelOptions.AsPrefix ? config.Advanced.LabelPrefixText : "";
            if (config.PercentageMode == CounterPercentageModes.Total || config.PercentageMode == CounterPercentageModes.TotalAndSplit)
                text += Format(98.76, config) + "\n";
            if (config.PercentageMode == CounterPercentageModes.Split || config.PercentageMode == CounterPercentageModes.TotalAndSplit)
            {
                string leftColor = config.SplitPercentageUseSaberColorScheme ? "<color=#FF5555>" : "";
                string rightColor = config.SplitPercentageUseSaberColorScheme ? "<color=#5555FF>" : "";
                text += $"{leftColor}{config.Advanced.PercentageSplitSaberAPrefixText}{Format(99.12, config)} " +
                    $"{rightColor}{config.Advanced.PercentageSplitSaberBPrefixText}{Format(98.41, config)}";
            }
            percentage.text = text;
        }

        private static string Format(double value, CounterSettings settings)
        {
            value = Math.Round(value, settings.DecimalPrecision);
            return (settings.KeepTrailingZeros
                ? value.ToString(settings.DecimalPrecision > 0 ? "0." + new string('0', settings.DecimalPrecision) : "0")
                : value.ToString()) + "%";
        }
    }
}
