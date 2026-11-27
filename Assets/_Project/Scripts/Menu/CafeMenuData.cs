using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[CreateAssetMenu(
    fileName = "CafeMenuData",
    menuName = "Remy's Cafe/Cafe Menu Data")]
public sealed class CafeMenuData : ScriptableObject
{
    [SerializeField] private List<CafeMenuItem> items = CreateDefaultItems();
    [SerializeField] private CafeSizePricing sizePricing = new CafeSizePricing(-50, 0, 75);
    [SerializeField] private List<CafeMilkCustomization> milkOptions = CreateDefaultMilkOptions();
    [SerializeField] private List<CafeMenuCustomization> extras = CreateDefaultExtras();

    public IReadOnlyList<CafeMenuItem> Items => items;
    public CafeSizePricing SizePricing => sizePricing;
    public IReadOnlyList<CafeMilkCustomization> MilkOptions => milkOptions;
    public IReadOnlyList<CafeMenuCustomization> Extras => extras;

    public int GetPriceForSize(CafeMenuItem item, CafeDrinkSize size)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (!item.HasSizes)
        {
            throw new InvalidOperationException(
                $"{item.DisplayName} does not support drink sizes.");
        }

        return checked(item.BasePriceCents + sizePricing.GetAdjustmentCents(size));
    }

    public static string FormatPrice(int cents)
    {
        decimal amount = cents / 100m;
        return amount.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    }

    private static List<CafeMenuItem> CreateDefaultItems()
    {
        return new List<CafeMenuItem>
        {
            new CafeMenuItem("Brewed Coffee", CafeMenuCategory.Coffee, 325, true, true, false, true),
            new CafeMenuItem("Americano", CafeMenuCategory.Coffee, 400, true, true, true, true),
            new CafeMenuItem("Cappuccino", CafeMenuCategory.Coffee, 525, true, true, false, true),
            new CafeMenuItem("Caffè Latte", CafeMenuCategory.Coffee, 525, true, true, true, true),
            new CafeMenuItem("Caffè Mocha", CafeMenuCategory.Coffee, 575, true, true, true, true),
            new CafeMenuItem("Caramel Macchiato", CafeMenuCategory.Coffee, 595, true, true, true, true),

            new CafeMenuItem("Iced Coffee", CafeMenuCategory.ColdDrinks, 400, true, false, true, true),
            new CafeMenuItem("Cold Brew", CafeMenuCategory.ColdDrinks, 475, true, false, true, true),
            new CafeMenuItem("Iced Latte", CafeMenuCategory.ColdDrinks, 525, true, false, true, true),

            new CafeMenuItem("Chai Latte", CafeMenuCategory.TeaAndOther, 525, true, true, true, true),
            new CafeMenuItem("Matcha Latte", CafeMenuCategory.TeaAndOther, 550, true, true, true, true),
            new CafeMenuItem("Hot Chocolate", CafeMenuCategory.TeaAndOther, 450, true, true, false, true),

            new CafeMenuItem("Butter Croissant", CafeMenuCategory.Bakery, 350, false, false, false, false),
            new CafeMenuItem("Chocolate Croissant", CafeMenuCategory.Bakery, 400, false, false, false, false),
            new CafeMenuItem("Blueberry Muffin", CafeMenuCategory.Bakery, 375, false, false, false, false),
            new CafeMenuItem("Chocolate Chip Cookie", CafeMenuCategory.Bakery, 300, false, false, false, false),

            new CafeMenuItem("Bacon, Egg & Cheese Sandwich", CafeMenuCategory.Breakfast, 625, false, false, false, false),
            new CafeMenuItem("Sausage, Egg & Cheese Sandwich", CafeMenuCategory.Breakfast, 625, false, false, false, false),
            new CafeMenuItem("Veggie & Egg Wrap", CafeMenuCategory.Breakfast, 575, false, false, false, false)
        };
    }

    private static List<CafeMilkCustomization> CreateDefaultMilkOptions()
    {
        return new List<CafeMilkCustomization>
        {
            new CafeMilkCustomization(CafeMilkOption.WholeMilk, "Whole milk", 0),
            new CafeMilkCustomization(CafeMilkOption.SkimMilk, "Skim milk", 0),
            new CafeMilkCustomization(CafeMilkOption.OatMilk, "Oat milk", 0),
            new CafeMilkCustomization(CafeMilkOption.AlmondMilk, "Almond milk", 0)
        };
    }

    private static List<CafeMenuCustomization> CreateDefaultExtras()
    {
        return new List<CafeMenuCustomization>
        {
            new CafeMenuCustomization(CafeExtraOption.ExtraEspressoShot, "Extra espresso shot", 100),
            new CafeMenuCustomization(CafeExtraOption.VanillaSyrup, "Vanilla syrup", 75),
            new CafeMenuCustomization(CafeExtraOption.CaramelSyrup, "Caramel syrup", 75)
        };
    }

    private void OnValidate()
    {
        ValidateItems();
        ValidateSizePricing();
        ValidateMilkOptions();
        ValidateExtras();
    }

    private void ValidateItems()
    {
        if (items == null)
        {
            Debug.LogWarning("[CafeMenuData] Menu item list is missing.", this);
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Count; i++)
        {
            CafeMenuItem item = items[i];

            if (item == null)
            {
                Debug.LogWarning($"[CafeMenuData] Menu item at index {i} is missing.", this);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.DisplayName))
            {
                Debug.LogWarning($"[CafeMenuData] Menu item at index {i} has no display name.", this);
            }
            else if (!names.Add(item.DisplayName.Trim()))
            {
                Debug.LogWarning(
                    $"[CafeMenuData] Duplicate menu item name: {item.DisplayName}.",
                    this);
            }

            if (item.BasePriceCents < 0)
            {
                Debug.LogWarning(
                    $"[CafeMenuData] {item.DisplayName} has a negative base price.",
                    this);
            }

            if (!item.HasSizes &&
                (item.SupportsHot || item.SupportsIced || item.SupportsMilkCustomization))
            {
                Debug.LogWarning(
                    $"[CafeMenuData] Unsized item {item.DisplayName} has drink-only capabilities.",
                    this);
            }
        }
    }

    private void ValidateSizePricing()
    {
        if (sizePricing == null)
        {
            Debug.LogWarning("[CafeMenuData] Size pricing is missing.", this);
            return;
        }

        if (sizePricing.MediumAdjustmentCents != 0)
        {
            Debug.LogWarning(
                "[CafeMenuData] Medium size should use the listed base price (0-cent adjustment).",
                this);
        }
    }

    private void ValidateMilkOptions()
    {
        if (milkOptions == null)
        {
            Debug.LogWarning("[CafeMenuData] Milk option list is missing.", this);
            return;
        }

        var options = new HashSet<CafeMilkOption>();

        for (int i = 0; i < milkOptions.Count; i++)
        {
            CafeMilkCustomization option = milkOptions[i];

            if (option == null)
            {
                Debug.LogWarning($"[CafeMenuData] Milk option at index {i} is missing.", this);
                continue;
            }

            if (string.IsNullOrWhiteSpace(option.DisplayName))
            {
                Debug.LogWarning($"[CafeMenuData] Milk option at index {i} has no display name.", this);
            }

            if (!options.Add(option.Option))
            {
                Debug.LogWarning($"[CafeMenuData] Duplicate milk option: {option.Option}.", this);
            }

            if (option.PriceAdjustmentCents < 0)
            {
                Debug.LogWarning(
                    $"[CafeMenuData] {option.DisplayName} has a negative price adjustment.",
                    this);
            }
        }
    }

    private void ValidateExtras()
    {
        if (extras == null)
        {
            Debug.LogWarning("[CafeMenuData] Extra customization list is missing.", this);
            return;
        }

        var options = new HashSet<CafeExtraOption>();

        for (int i = 0; i < extras.Count; i++)
        {
            CafeMenuCustomization option = extras[i];

            if (option == null)
            {
                Debug.LogWarning($"[CafeMenuData] Extra at index {i} is missing.", this);
                continue;
            }

            if (string.IsNullOrWhiteSpace(option.DisplayName))
            {
                Debug.LogWarning($"[CafeMenuData] Extra at index {i} has no display name.", this);
            }

            if (!options.Add(option.Option))
            {
                Debug.LogWarning($"[CafeMenuData] Duplicate extra: {option.Option}.", this);
            }

            if (option.PriceAdjustmentCents < 0)
            {
                Debug.LogWarning(
                    $"[CafeMenuData] {option.DisplayName} has a negative price adjustment.",
                    this);
            }
        }
    }
}
