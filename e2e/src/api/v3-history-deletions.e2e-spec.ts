import { randomBytes } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

// Nightscout's v3 DELETE keeps the document with `isValid: false` and a new `srvModified`, and
// `history` returns it, which is how a client syncing through history (AAPS) learns of a delete.

const MINUTE = 60_000;

interface V3Envelope<T> {
  status: number;
  result: T[];
}

interface V3Document {
  identifier?: string;
  _id?: string;
  srvModified: number;
  isValid?: boolean;
  insulin?: number;
  sgv?: number;
  device?: string;
}

interface LastModified {
  result: { collections: Record<string, number> };
}

const objectId = () => randomBytes(12).toString("hex");
const idOf = (doc: V3Document) => doc.identifier ?? doc._id;
const cursorOf = (etag: string | null) => Number(etag!.match(/"(\d+)"/)![1]);

async function history(tenant: Tenant, collection: string, since: number) {
  const res = await tenant.api.get<V3Envelope<V3Document>>(`/api/v3/${collection}/history/${since}?limit=1000`);
  expect(res.status).toBe(200);
  return { docs: res.body.result, cursor: cursorOf(res.headers.get("etag")) };
}

/**
 * Reads `collection`'s history until `match` shows up live, deletes it by the identifier history
 * served, and returns the next history page from the cursor the first read left the client at.
 */
async function deleteAfterSync(tenant: Tenant, collection: string, match: (doc: V3Document) => boolean) {
  const synced = await history(tenant, collection, 0);
  const live = synced.docs.find(match);
  expect(live).toBeDefined();
  expect(live!.isValid).not.toBe(false);

  const deleted = await tenant.api.delete(`/api/v3/${collection}/${idOf(live!)}`);
  expect(deleted.status).toBeLessThan(300);

  const next = await history(tenant, collection, synced.cursor);
  return { live: live!, cursor: synced.cursor, next };
}

describe("v3 history after a delete", () => {
  let tenant: Tenant;
  const date = Math.floor((Date.now() - 15 * MINUTE) / 1000) * 1000;
  const iso = new Date(date).toISOString();

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("serves a deleted entry with isValid false, stamped after the client's cursor", async () => {
    // workaround: #1798 - an entry uploaded without an id is stored under one its v3 DELETE cannot
    // reach, so this upload names its own ObjectId.
    const id = objectId();
    const created = await tenant.api.request("POST", "/api/v3/entries", {
      _id: id, type: "sgv", sgv: 187, date, direction: "Flat", device: "AAPS-e2e-delete", app: "AAPS", utcOffset: 0,
    });
    expect(created.status).toBeLessThan(300);

    const { live, cursor, next } = await deleteAfterSync(tenant, "entries", (e) => e.sgv === 187 && e.device === "AAPS-e2e-delete");

    const tombstone = next.docs.find((e) => idOf(e) === idOf(live));
    expect(tombstone).toMatchObject({ isValid: false, sgv: 187 });
    expect(tombstone!.srvModified).toBeGreaterThan(cursor);
    expect(next.cursor).toBeGreaterThanOrEqual(tombstone!.srvModified);
  });

  it("serves a deleted treatment with isValid false, stamped after the client's cursor", async () => {
    const created = await tenant.api.request("POST", "/api/v3/treatments", {
      eventType: "Correction Bolus", insulin: 1.35, date, app: "AAPS", device: "AAPS-e2e-delete", utcOffset: 0, type: "NORMAL",
    });
    expect(created.status).toBe(201);

    const { live, cursor, next } = await deleteAfterSync(tenant, "treatments", (t) => t.insulin === 1.35);

    const tombstone = next.docs.find((t) => idOf(t) === idOf(live));
    expect(tombstone).toMatchObject({ isValid: false, insulin: 1.35 });
    expect(tombstone!.srvModified).toBeGreaterThan(cursor);
  });

  it("serves a deleted devicestatus with isValid false, stamped after the client's cursor", async () => {
    // workaround: #1798 - a devicestatus uploaded without an id is stored under one its v3 DELETE
    // cannot reach, so this upload names its own ObjectId.
    const id = objectId();
    const created = await tenant.api.request("POST", "/api/v3/devicestatus", {
      _id: id, date, created_at: iso, device: "openaps://AAPS-e2e-delete", app: "AAPS", utcOffset: 0,
      openaps: { iob: { iob: 0.4, time: iso }, suggested: { bg: 125, eventualBG: 118, reason: "e2e delete", timestamp: iso } },
    });
    expect(created.status).toBeLessThan(300);

    const { live, cursor, next } = await deleteAfterSync(tenant, "devicestatus", (d) => d.device === "openaps://AAPS-e2e-delete");
    expect(idOf(live)).toBe(id);

    const tombstone = next.docs.find((d) => idOf(d) === id);
    expect(tombstone).toMatchObject({ isValid: false });
    expect(tombstone!.srvModified).toBeGreaterThan(cursor);
  });

  it("moves each collection's lastModified to the delete, so a client knows to read the history", async () => {
    const before = await tenant.api.ok<LastModified>("GET", "/api/v3/lastModified");
    const created = await tenant.api.request("POST", "/api/v3/treatments", {
      eventType: "Correction Bolus", insulin: 2.15, date, app: "AAPS", device: "AAPS-e2e-delete", utcOffset: 0, type: "NORMAL",
    });
    expect(created.status).toBe(201);

    const { next } = await deleteAfterSync(tenant, "treatments", (t) => t.insulin === 2.15);
    const tombstone = next.docs.find((t) => t.insulin === 2.15 && t.isValid === false);
    expect(tombstone).toBeDefined();

    const after = await tenant.api.ok<LastModified>("GET", "/api/v3/lastModified");
    expect(after.result.collections.treatments).toBeGreaterThanOrEqual(tombstone!.srvModified);
    expect(after.result.collections.treatments).toBeGreaterThan(before.result.collections.treatments ?? 0);
  });
});
