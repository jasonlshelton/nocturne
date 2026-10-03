import { createHash, randomUUID } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import type { ApiClient } from "../helpers/http.ts";
import { minutesAgo } from "../helpers/data.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

// Loop (NightscoutKit) edits a treatment with PUT /api/v1/treatments, one object per request, the
// record's `_id` in the body, authenticated by the SHA-1 of its API secret. An override's `_id` is
// Loop's own UUID. Nightscout answers 200 with the saved document and inserts an unknown `_id`.

interface DirectGrant {
  token: string;
}

interface SavedTreatment {
  _id: string;
  eventType: string;
  duration?: number;
}

interface StateSpan {
  originalId?: string;
  startMills: number;
  endMills?: number;
}

interface Page<T> {
  data: T[];
}

/** An override as NightscoutKit's OverrideTreatment serialises it. */
function loopOverride(id: string, startedAt: string, extra: Record<string, unknown>) {
  return {
    _id: id,
    eventType: "Temporary Override",
    created_at: startedAt,
    timestamp: startedAt,
    enteredBy: "Loop",
    reason: "Running",
    correctionRange: [140, 160],
    insulinNeedsScaleFactor: 0.8,
    ...extra,
  };
}

describe("v1 PUT /treatments, as Loop sends it", () => {
  let tenant: Tenant;
  let loop: ApiClient;

  const overridesFor = async (id: string) => {
    const from = new Date(Date.now() - 6 * 60 * 60_000).toISOString();
    const page = await tenant.api.ok<Page<StateSpan>>("GET", `/api/v4/state-spans/overrides?from=${from}&limit=1000`);
    return page.data.filter((s) => s.originalId?.toLowerCase() === id.toLowerCase());
  };

  beforeAll(async () => {
    tenant = await seedTenant();
    const grant = await tenant.api.ok<DirectGrant>("POST", "/api/auth/direct-grants", {
      label: "e2e loop",
      scopes: ["glucose.readwrite", "treatments.readwrite", "devices.readwrite", "therapy.readwrite"],
    });
    loop = tenant.anonymous.with({ kind: "api-secret", secret: createHash("sha1").update(grant.token).digest("hex") });
  });

  it("updates an override in place by its UUID _id", async () => {
    const id = randomUUID().toUpperCase();
    const startedAt = minutesAgo(30);
    await loop.ok("POST", "/api/v1/treatments", [loopOverride(id, startedAt, { durationType: "indefinite" })]);

    const put = await loop.put<SavedTreatment>("/api/v1/treatments", loopOverride(id, startedAt, { duration: 20 }));

    expect(put.status, put.text).toBe(200);
    expect(Array.isArray(put.body)).toBe(false);
    expect(put.body).toMatchObject({ eventType: "Temporary Override", duration: 20 });

    const spans = await overridesFor(id);
    expect(spans).toHaveLength(1);
    expect(Math.abs(spans[0]!.startMills - Date.parse(startedAt))).toBeLessThan(1000);
    expect(spans[0]!.endMills! - spans[0]!.startMills).toBe(20 * 60_000);
  });

  it("inserts an override whose _id the server has never seen", async () => {
    const id = randomUUID().toUpperCase();
    const startedAt = minutesAgo(90);

    const put = await loop.put<SavedTreatment>("/api/v1/treatments", loopOverride(id, startedAt, { duration: 45 }));

    expect(put.status, put.text).toBe(200);
    expect(put.body).toMatchObject({ eventType: "Temporary Override", duration: 45 });
    const spans = await overridesFor(id);
    expect(spans).toHaveLength(1);
    expect(Math.abs(spans[0]!.startMills - Date.parse(startedAt))).toBeLessThan(1000);
    expect(spans[0]!.endMills! - spans[0]!.startMills).toBe(45 * 60_000);
  });

  it("refuses an array, which Nightscout's save never accepted", async () => {
    const put = await loop.put("/api/v1/treatments", [loopOverride(randomUUID(), minutesAgo(10), { duration: 5 })]);
    expect(put.status).toBe(400);
  });
});
