using System;
using System.Collections.Generic;
using System.Text;

public static class CafeMenuLlmFormatter
{
    public static string BuildContext(MenuManager menuManager)
    {
        if (menuManager == null)
        {
            throw new ArgumentNullException(nameof(menuManager));
        }

        if (!menuManager.IsConfigured)
        {
            throw new InvalidOperationException(
                "Cannot build LLM menu context without configured menu data.");
        }

        IReadOnlyList<CafeMenuItem> items = menuManager.GetAllItems();

        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                "Cannot build LLM menu context from an empty menu.");
        }

        var builder = new StringBuilder(4096);

        builder.AppendLine("AUTHORITATIVE CAFE MENU")
            .AppendLine("All products, options, and prices below come from the game menu and are authoritative.")
            .AppendLine("Only offer products and customizations explicitly listed here.")
            .AppendLine("Use only these prices and adjustments. Calculate the total of the current conversational order when appropriate; recalculate after changes.")
            .AppendLine()
            .AppendLine("PRODUCTS BY CATEGORY");

        Array categories = Enum.GetValues(typeof(CafeMenuCategory));

        for (int categoryIndex = 0;
             categoryIndex < categories.Length;
             categoryIndex++)
        {
            var category = (CafeMenuCategory)categories.GetValue(categoryIndex);
            AppendCategory(builder, menuManager, items, category);
        }

        AppendSizeRules(builder, menuManager);
        AppendMilkOptions(builder, menuManager);
        AppendExtras(builder, menuManager);

        return builder.ToString().TrimEnd();
    }

    private static void AppendCategory(
        StringBuilder builder,
        MenuManager menuManager,
        IReadOnlyList<CafeMenuItem> items,
        CafeMenuCategory category)
    {
        bool headingWritten = false;

        for (int i = 0; i < items.Count; i++)
        {
            CafeMenuItem item = items[i];

            if (item == null)
            {
                throw new InvalidOperationException(
                    $"The menu contains a null item at index {i}.");
            }

            if (item.Category != category)
            {
                continue;
            }

            if (!headingWritten)
            {
                builder.AppendLine()
                    .Append(SplitPascalCase(category.ToString()))
                    .AppendLine(":");
                headingWritten = true;
            }

            AppendItem(builder, menuManager, item);
        }
    }

    private static void AppendItem(
        StringBuilder builder,
        MenuManager menuManager,
        CafeMenuItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DisplayName))
        {
            throw new InvalidOperationException(
                "The menu contains an item without a display name.");
        }

        builder.Append("- ")
            .Append(item.DisplayName)
            .Append(" | Listed price: ")
            .Append(MenuManager.FormatPrice(item.BasePriceCents));

        if (item.HasSizes)
        {
            builder.Append(" | Sizes: Small ")
                .Append(MenuManager.FormatPrice(
                    menuManager.GetPriceForSize(item, CafeDrinkSize.Small)))
                .Append(", Medium ")
                .Append(MenuManager.FormatPrice(
                    menuManager.GetPriceForSize(item, CafeDrinkSize.Medium)))
                .Append(", Large ")
                .Append(MenuManager.FormatPrice(
                    menuManager.GetPriceForSize(item, CafeDrinkSize.Large)));
        }
        else
        {
            builder.Append(" | Sizes: not available");
        }

        if (item.SupportsHot || item.SupportsIced)
        {
            builder.Append(" | Serving: ");

            if (item.SupportsHot && item.SupportsIced)
            {
                builder.Append("hot or iced");
            }
            else if (item.SupportsHot)
            {
                builder.Append("hot only");
            }
            else
            {
                builder.Append("iced only");
            }
        }

        builder.Append(" | Milk customization: ")
            .Append(item.SupportsMilkCustomization
                ? "available"
                : "not available")
            .AppendLine();
    }

    private static void AppendSizeRules(
        StringBuilder builder,
        MenuManager menuManager)
    {
        CafeSizePricing pricing = menuManager.GetSizePricing();

        if (pricing == null)
        {
            throw new InvalidOperationException(
                "The menu has no size-pricing configuration.");
        }

        builder.AppendLine()
            .AppendLine("SIZE PRICE RULES")
            .AppendLine("These adjustments apply only to products marked with Small, Medium, and Large sizes.")
            .Append("- Small: listed price ")
            .AppendLine(FormatSignedPrice(pricing.SmallAdjustmentCents))
            .Append("- Medium: listed price ")
            .AppendLine(FormatSignedPrice(pricing.MediumAdjustmentCents))
            .Append("- Large: listed price ")
            .AppendLine(FormatSignedPrice(pricing.LargeAdjustmentCents));
    }

    private static void AppendMilkOptions(
        StringBuilder builder,
        MenuManager menuManager)
    {
        IReadOnlyList<CafeMilkCustomization> milkOptions =
            menuManager.GetMilkOptions();

        builder.AppendLine()
            .AppendLine("MILK OPTIONS")
            .AppendLine("These are the only milk options, and they apply only to products marked as allowing milk customization.");

        if (milkOptions.Count == 0)
        {
            builder.AppendLine("- None");
            return;
        }

        for (int i = 0; i < milkOptions.Count; i++)
        {
            CafeMilkCustomization option = milkOptions[i];

            if (option == null || string.IsNullOrWhiteSpace(option.DisplayName))
            {
                throw new InvalidOperationException(
                    $"The menu contains an invalid milk option at index {i}.");
            }

            builder.Append("- ")
                .Append(option.DisplayName)
                .Append(": ")
                .AppendLine(FormatSignedPrice(option.PriceAdjustmentCents));
        }
    }

    private static void AppendExtras(
        StringBuilder builder,
        MenuManager menuManager)
    {
        IReadOnlyList<CafeMenuCustomization> extras =
            menuManager.GetExtras();

        builder.AppendLine()
            .AppendLine("EXTRAS")
            .AppendLine("These are the only extras in the current menu. Do not invent other extras.");

        if (extras.Count == 0)
        {
            builder.AppendLine("- None");
            return;
        }

        for (int i = 0; i < extras.Count; i++)
        {
            CafeMenuCustomization extra = extras[i];

            if (extra == null || string.IsNullOrWhiteSpace(extra.DisplayName))
            {
                throw new InvalidOperationException(
                    $"The menu contains an invalid extra at index {i}.");
            }

            builder.Append("- ")
                .Append(extra.DisplayName)
                .Append(": ")
                .AppendLine(FormatSignedPrice(extra.PriceAdjustmentCents));
        }
    }

    private static string FormatSignedPrice(int cents)
    {
        if (cents > 0)
        {
            return "+" + MenuManager.FormatPrice(cents);
        }

        if (cents < 0)
        {
            return "-" + MenuManager.FormatPrice(checked(-cents));
        }

        return "+" + MenuManager.FormatPrice(0);
    }

    private static string SplitPascalCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];

            if (i > 0 && char.IsUpper(current))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
