using System;
using UnityEngine;

public enum CafeMilkOption
{
    WholeMilk,
    SkimMilk,
    OatMilk,
    AlmondMilk
}

public enum CafeExtraOption
{
    ExtraEspressoShot,
    VanillaSyrup,
    CaramelSyrup
}

[Serializable]
public sealed class CafeMilkCustomization
{
    [SerializeField] private CafeMilkOption option;
    [SerializeField] private string displayName;
    [SerializeField, Min(0)] private int priceAdjustmentCents;

    public CafeMilkOption Option => option;
    public string DisplayName => displayName;
    public int PriceAdjustmentCents => priceAdjustmentCents;

    public CafeMilkCustomization(
        CafeMilkOption option,
        string displayName,
        int priceAdjustmentCents)
    {
        this.option = option;
        this.displayName = displayName;
        this.priceAdjustmentCents = priceAdjustmentCents;
    }
}

[Serializable]
public sealed class CafeMenuCustomization
{
    [SerializeField] private CafeExtraOption option;
    [SerializeField] private string displayName;
    [SerializeField, Min(0)] private int priceAdjustmentCents;

    public CafeExtraOption Option => option;
    public string DisplayName => displayName;
    public int PriceAdjustmentCents => priceAdjustmentCents;

    public CafeMenuCustomization(
        CafeExtraOption option,
        string displayName,
        int priceAdjustmentCents)
    {
        this.option = option;
        this.displayName = displayName;
        this.priceAdjustmentCents = priceAdjustmentCents;
    }
}

[Serializable]
public sealed class CafeSizePricing
{
    [SerializeField] private int smallAdjustmentCents = -50;
    [SerializeField] private int mediumAdjustmentCents;
    [SerializeField] private int largeAdjustmentCents = 75;

    public int SmallAdjustmentCents => smallAdjustmentCents;
    public int MediumAdjustmentCents => mediumAdjustmentCents;
    public int LargeAdjustmentCents => largeAdjustmentCents;

    public CafeSizePricing(
        int smallAdjustmentCents,
        int mediumAdjustmentCents,
        int largeAdjustmentCents)
    {
        this.smallAdjustmentCents = smallAdjustmentCents;
        this.mediumAdjustmentCents = mediumAdjustmentCents;
        this.largeAdjustmentCents = largeAdjustmentCents;
    }

    public int GetAdjustmentCents(CafeDrinkSize size)
    {
        switch (size)
        {
            case CafeDrinkSize.Small:
                return smallAdjustmentCents;
            case CafeDrinkSize.Medium:
                return mediumAdjustmentCents;
            case CafeDrinkSize.Large:
                return largeAdjustmentCents;
            default:
                throw new ArgumentOutOfRangeException(nameof(size), size, null);
        }
    }
}
