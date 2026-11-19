namespace WorldGeneration;

/// <summary>
/// Immutable recipes. IDs are sent over the network; the server uses these
/// definitions rather than accepting costs or outputs supplied by clients.
/// Type 0 is only a sentinel for a recipe without a second ingredient.
/// </summary>
public sealed record CraftingRecipe(
    int Id, string Name,
    int InputA, int CountA,
    int InputB, int CountB,
    int Output, int CountOut);

public static class CraftingRecipes
{
    private static readonly CraftingRecipe[] Recipes =
    {
        new(1, "Wood planks", 4, 1, 0, 0, 6, 4),
        new(2, "Grass blocks", 2, 2, 9, 1, 1, 2),
        new(3, "Stone forge", 3, 8, 8, 2, 5, 1),
        // Recipe IDs 4 and 5 are Forge-only. BlockEditor.CraftRecipe
        // explicitly rejects them; the server checks proximity to an
        // actual forge through the separate SmeltIron RPC.
        new(4, "Iron ingot", 8, 1, 7, 1, BlockInventory.IronIngot, 1),
        new(5, "Five iron ingots", 8, 5, 7, 5, BlockInventory.IronIngot, 5),
    };

    public static System.Collections.Generic.IReadOnlyList<CraftingRecipe> All => Recipes;

    public static CraftingRecipe? Find(int id)
    {
        // Bounded, immutable table: invalid or forged client IDs do nothing.
        foreach (CraftingRecipe recipe in Recipes)
            if (recipe.Id == id)
                return recipe;
        return null;
    }
}
