import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";

const removeBolus = vi.hoisted(() => vi.fn());

vi.mock("$api/generated/bolus.generated.remote", () => {
  const form = () => ({ enhance: () => ({}), result: undefined });
  return { create: form(), update: form(), remove: removeBolus };
});
vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getInsulins: () => ({ current: [] }),
}));

import type { RealtimeStore } from "$lib/stores/realtime-store.svelte";
import Wrapper from "./meal-bolus-dialog-test-wrapper.svelte";

// The dialog is portaled and its icon buttons carry no names.
const trashButton = () =>
  document.querySelector<HTMLButtonElement>("svg.lucide-trash-2")?.closest("button") ?? null;

describe("MealBolusDialog", () => {
  it("tells the realtime store a treatment changed once a delete succeeds", async () => {
    removeBolus.mockResolvedValue(undefined);
    let store!: RealtimeStore;
    const onSave = vi.fn();
    render(Wrapper, {
      props: {
        meal: { boluses: [{ id: "b1", mills: Date.now(), insulin: 4 }] },
        onstore: (s: RealtimeStore) => (store = s),
        onSave,
      },
    });
    const before = store.treatmentRevision;

    await vi.waitFor(() => expect(trashButton()).not.toBeNull());
    trashButton()!.click();
    await page.getByRole("button", { name: "Delete" }).click();

    await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before + 1);
  });

  it("leaves the store alone when the delete fails", async () => {
    removeBolus.mockRejectedValue(new Error("nope"));
    let store!: RealtimeStore;
    render(Wrapper, {
      props: {
        meal: { boluses: [{ id: "b1", mills: Date.now(), insulin: 4 }] },
        onstore: (s: RealtimeStore) => (store = s),
        onSave: vi.fn(),
      },
    });
    const before = store.treatmentRevision;

    await vi.waitFor(() => expect(trashButton()).not.toBeNull());
    trashButton()!.click();
    await page.getByRole("button", { name: "Delete" }).click();
    await vi.waitFor(() => expect(removeBolus).toHaveBeenCalled());

    expect(store.treatmentRevision).toBe(before);
  });
});
