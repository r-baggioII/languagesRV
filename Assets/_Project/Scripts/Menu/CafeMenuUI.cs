using System;
using System.Text;
using TMPro;
using UnityEngine;

public sealed class CafeMenuUI : MonoBehaviour
{
    [SerializeField] private CafeMenuData menuData;
    [SerializeField] private TMP_Text leftColumnText;
    [SerializeField] private TMP_Text rightColumnText;

    private void OnEnable()
    {
        RefreshMenu();
    }

    public void RefreshMenu()
    {
        bool hasMissingReference = false;

        if (menuData == null)
        {
            Debug.LogError("[CafeMenuUI] CafeMenuData reference is not assigned.", this);
            hasMissingReference = true;
        }

        if (leftColumnText == null)
        {
            Debug.LogError("[CafeMenuUI] Left-column TMP_Text reference is not assigned.", this);
            hasMissingReference = true;
        }

        if (rightColumnText == null)
        {
            Debug.LogError("[CafeMenuUI] Right-column TMP_Text reference is not assigned.", this);
            hasMissingReference = true;
        }

        if (hasMissingReference)
        {
            return;
        }

        leftColumnText.text = BuildLeftColumnText();
        rightColumnText.text = BuildRightColumnText();
    }

    private string BuildLeftColumnText()
    {
        var builder = new StringBuilder(768);

        AppendCategory(builder, CafeMenuCategory.Coffee, "COFFEE");
        AppendCategory(builder, CafeMenuCategory.ColdDrinks, "COLD DRINKS");
        AppendCategory(builder, CafeMenuCategory.TeaAndOther, "TEA & OTHER");

        return builder.ToString().TrimEnd();
    }

    private string BuildRightColumnText()
    {
        var builder = new StringBuilder(768);

        AppendCategory(builder, CafeMenuCategory.Bakery, "BAKERY");
        AppendCategory(builder, CafeMenuCategory.Breakfast, "BREAKFAST");
        AppendSizes(builder);
        AppendMilkOptions(builder);
        AppendExtras(builder);

        return builder.ToString().TrimEnd();
    }

    private void AppendCategory(
        StringBuilder builder,
        CafeMenuCategory category,
        string heading)
    {
        AppendHeading(builder, heading);

        if (menuData.Items != null)
        {
            for (int i = 0; i < menuData.Items.Count; i++)
            {
                CafeMenuItem item = menuData.Items[i];

                if (item == null || item.Category != category)
                {
                    continue;
                }

                builder
                    .Append(item.DisplayName)
                    .Append(" — ")
                    .AppendLine(CafeMenuData.FormatPrice(item.BasePriceCents));
            }
        }

        builder.AppendLine();
    }

    private void AppendSizes(StringBuilder builder)
    {
        AppendHeading(builder, "SIZES");

        CafeSizePricing sizePricing = menuData.SizePricing;

        if (sizePricing == null)
        {
            Debug.LogError("[CafeMenuUI] CafeMenuData has no size-pricing configuration.", this);
            builder.AppendLine("Size pricing unavailable");
            builder.AppendLine();
            return;
        }

        AppendAdjustment(
            builder,
            CafeDrinkSize.Small.ToString(),
            sizePricing.SmallAdjustmentCents);

        builder.Append(CafeDrinkSize.Medium).Append(" — listed price");

        if (sizePricing.MediumAdjustmentCents != 0)
        {
            builder
                .Append(" (")
                .Append(FormatSignedAdjustment(sizePricing.MediumAdjustmentCents))
                .Append(')');
        }

        builder.AppendLine();

        AppendAdjustment(
            builder,
            CafeDrinkSize.Large.ToString(),
            sizePricing.LargeAdjustmentCents);

        builder.AppendLine();
    }

    private void AppendMilkOptions(StringBuilder builder)
    {
        AppendHeading(builder, "MILK");

        if (menuData.MilkOptions == null)
        {
            Debug.LogError("[CafeMenuUI] CafeMenuData has no milk-option collection.", this);
            builder.AppendLine("Milk options unavailable");
            builder.AppendLine();
            return;
        }

        for (int i = 0; i < menuData.MilkOptions.Count; i++)
        {
            CafeMilkCustomization milkOption = menuData.MilkOptions[i];

            if (milkOption == null)
            {
                continue;
            }

            builder.Append(milkOption.DisplayName);

            if (milkOption.PriceAdjustmentCents != 0)
            {
                builder
                    .Append(" — ")
                    .Append(FormatSignedAdjustment(milkOption.PriceAdjustmentCents));
            }

            builder.AppendLine();
        }

        builder.AppendLine();
    }

    private void AppendExtras(StringBuilder builder)
    {
        AppendHeading(builder, "EXTRAS");

        if (menuData.Extras == null)
        {
            Debug.LogError("[CafeMenuUI] CafeMenuData has no extras collection.", this);
            builder.AppendLine("Extras unavailable");
            return;
        }

        for (int i = 0; i < menuData.Extras.Count; i++)
        {
            CafeMenuCustomization extra = menuData.Extras[i];

            if (extra == null)
            {
                continue;
            }

            builder
                .Append(extra.DisplayName)
                .Append(" — ")
                .AppendLine(FormatSignedAdjustment(extra.PriceAdjustmentCents));
        }
    }

    private static void AppendHeading(StringBuilder builder, string heading)
    {
        builder.Append("<b>").Append(heading).AppendLine("</b>");
    }

    private static void AppendAdjustment(
        StringBuilder builder,
        string label,
        int adjustmentCents)
    {
        builder
            .Append(label)
            .Append(" — ")
            .AppendLine(FormatSignedAdjustment(adjustmentCents));
    }

    private static string FormatSignedAdjustment(int adjustmentCents)
    {
        if (adjustmentCents > 0)
        {
            return "+" + CafeMenuData.FormatPrice(adjustmentCents);
        }

        if (adjustmentCents < 0)
        {
            return "-" + CafeMenuData.FormatPrice(Math.Abs(adjustmentCents));
        }

        return CafeMenuData.FormatPrice(0);
    }
}

