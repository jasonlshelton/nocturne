import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { MealEvent, SuggestedMealMatch } from "$lib/api";
import {
  remoteCommand,
  remoteQuery,
  updatedBy,
} from "$lib/test-stubs/remote-resource";

const meals: MealEvent[] = [
  {
    isAttributed: false,
    totalCarbs: 40,
    carbIntakes: [{ id: "carb-1", mills: 1_700_000_000_000, carbs: 40 }],
    foods: [],
  } as MealEvent,
];

const suggestions: SuggestedMealMatch[] = [
  {
    foodEntryId: "food-1",
    foodName: "Porridge",
    carbs: 40,
    carbIntakeId: "carb-1",
    matchScore: 0.9,
  },
];

vi.mock("$api/generated/nutritions.generated.remote", () => ({
  getMeals: () => remoteQuery(() => meals),
  addCarbIntakeFood: remoteCommand(() => Promise.resolve()),
  deleteCarbIntakeFood: remoteCommand(() => Promise.resolve()),
  updateCarbIntakeFood: remoteCommand(() => Promise.resolve()),
}));

vi.mock("$api/generated/mealMatchings.generated.remote", () => ({
  getSuggestions: () => remoteQuery(() => suggestions),
  getFoodEntry: () => remoteQuery(() => undefined),
  acceptMatch: remoteCommand(() => Promise.resolve()),
  dismissMatch: remoteCommand(() => Promise.resolve()),
}));

vi.mock("sveltekit-search-params", async () => {
  const { writable } = await import("svelte/store");
  return { queryParam: () => writable(null) };
});

vi.mock("svelte-sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

import { getMeals } from "$api/generated/nutritions.generated.remote";
import {
  getSuggestions,
  acceptMatch,
  dismissMatch,
} from "$api/generated/mealMatchings.generated.remote";
import MealsPage from "./+page.svelte";

describe("meals page", () => {
  beforeEach(() => {
    updatedBy(acceptMatch).length = 0;
    updatedBy(dismissMatch).length = 0;
  });

  it("refreshes the suggestions and the meals the page reads when a match is accepted", async () => {
    render(MealsPage, {});

    await page.getByRole("button", { name: "Accept" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(acceptMatch)).toEqual([[getSuggestions, getMeals]])
    );
  });

  it("refreshes the suggestions the page reads when a match is dismissed", async () => {
    render(MealsPage, {});

    await page.getByRole("button", { name: "Dismiss" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(dismissMatch)).toEqual([[getSuggestions]])
    );
  });
});
