#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const contractRoot = join(repositoryRoot, "contracts", "events");
const catalogPath = join(contractRoot, "catalog.json");
const baselineArgument = process.argv.find((argument) => argument.startsWith("--baseline-ref="));
const baselineRef = baselineArgument?.slice("--baseline-ref=".length);
const failures = [];

function fail(message) {
  failures.push(message);
}

function parseJson(path, source = readFileSync(path, "utf8")) {
  try {
    return JSON.parse(source);
  } catch (error) {
    fail(`${relative(repositoryRoot, path)} is not valid JSON: ${error.message}`);
    return null;
  }
}

function listJsonSchemas(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return listJsonSchemas(path);
    }
    return entry.name.endsWith(".schema.json") ? [path] : [];
  });
}

function normalizedTypes(value) {
  if (value === undefined) {
    return null;
  }
  return new Set(Array.isArray(value) ? value : [value]);
}

function compareCompatibility(previous, current, path, contractPath) {
  if (previous === null || typeof previous !== "object") {
    return;
  }
  if (current === null || typeof current !== "object") {
    fail(`${contractPath}: ${path} was removed or changed from an object`);
    return;
  }

  const previousTypes = normalizedTypes(previous.type);
  const currentTypes = normalizedTypes(current.type);
  if (previousTypes && (!currentTypes || [...previousTypes].some((type) => !currentTypes.has(type)))) {
    fail(`${contractPath}: ${path}.type was narrowed; publish a new schema version`);
  }

  if (Object.hasOwn(previous, "const") && previous.const !== current.const) {
    fail(`${contractPath}: ${path}.const changed; publish a new schema version`);
  }

  if (Array.isArray(previous.enum)) {
    if (!Array.isArray(current.enum) || previous.enum.some((value) => !current.enum.includes(value))) {
      fail(`${contractPath}: ${path}.enum removed accepted values; publish a new schema version`);
    }
  }

  const previousRequired = [...(previous.required ?? [])].sort();
  const currentRequired = [...(current.required ?? [])].sort();
  if (JSON.stringify(previousRequired) !== JSON.stringify(currentRequired)) {
    fail(`${contractPath}: ${path}.required changed; publish a new schema version`);
  }

  if (previous.additionalProperties !== false && current.additionalProperties === false) {
    fail(`${contractPath}: ${path}.additionalProperties was narrowed; publish a new schema version`);
  }

  for (const [name, previousProperty] of Object.entries(previous.properties ?? {})) {
    const currentProperty = current.properties?.[name];
    if (!currentProperty) {
      fail(`${contractPath}: ${path}.properties.${name} was removed; publish a new schema version`);
      continue;
    }
    compareCompatibility(
      previousProperty,
      currentProperty,
      `${path}.properties.${name}`,
      contractPath,
    );
  }

  if (Array.isArray(previous.allOf)) {
    if (!Array.isArray(current.allOf) || current.allOf.length < previous.allOf.length) {
      fail(`${contractPath}: ${path}.allOf was removed; publish a new schema version`);
    } else {
      previous.allOf.forEach((node, index) =>
        compareCompatibility(node, current.allOf[index], `${path}.allOf[${index}]`, contractPath),
      );
    }
  }
}

function readFromGit(ref, path) {
  const repositoryPath = relative(repositoryRoot, path);
  try {
    return execFileSync("git", ["show", `${ref}:${repositoryPath}`], {
      cwd: repositoryRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    });
  } catch {
    return null;
  }
}

const catalog = parseJson(catalogPath);
const schemas = new Map();

for (const schemaPath of listJsonSchemas(contractRoot)) {
  const schema = parseJson(schemaPath);
  if (!schema) {
    continue;
  }
  schemas.set(relative(contractRoot, schemaPath), { path: schemaPath, schema });

  for (const part of schema.allOf ?? []) {
    if (typeof part.$ref === "string" && part.$ref.startsWith(".")) {
      const referencedPath = resolve(dirname(schemaPath), part.$ref);
      if (!existsSync(referencedPath)) {
        fail(`${relative(repositoryRoot, schemaPath)} references missing schema ${part.$ref}`);
      }
    }
  }
}

if (catalog) {
  const seenContracts = new Set();
  for (const event of catalog.events ?? []) {
    const contractKey = `${event.event_type}@${event.schema_version}`;
    if (seenContracts.has(contractKey)) {
      fail(`catalog contains duplicate contract ${contractKey}`);
    }
    seenContracts.add(contractKey);

    if (event.status !== "active") {
      fail(`${contractKey} must use status=active or move to planned_event_types`);
    }
    if (!event.producer || !Array.isArray(event.consumers) || !Array.isArray(event.transports)) {
      fail(`${contractKey} must declare producer, consumers, and transports`);
    }

    const schemaEntry = schemas.get(event.schema);
    if (!schemaEntry) {
      fail(`${contractKey} references missing schema ${event.schema}`);
      continue;
    }
    if (schemaEntry.schema["x-schema-version"] !== event.schema_version) {
      fail(`${contractKey} does not match x-schema-version in ${event.schema}`);
    }
    if (!schemaEntry.schema["x-event-types"]?.includes(event.event_type)) {
      fail(`${contractKey} is not declared by ${event.schema}`);
    }
  }

  const planned = new Set(catalog.planned_event_types ?? []);
  for (const eventType of planned) {
    if ([...seenContracts].some((contract) => contract.startsWith(`${eventType}@`))) {
      fail(`${eventType} cannot be both active and planned`);
    }
  }
}

const envelope = schemas.get("v1/event-envelope.schema.json")?.schema;
const requiredEnvelopeFields = [
  "event_id",
  "event_type",
  "schema_version",
  "occurred_at",
  "correlation_id",
  "causation_id",
  "workspace_id",
  "producer",
  "payload",
];
for (const field of requiredEnvelopeFields) {
  if (!envelope?.required?.includes(field) || !envelope?.properties?.[field]) {
    fail(`v1 event envelope must require and define ${field}`);
  }
}

if (baselineRef && !/^0+$/.test(baselineRef)) {
  const previousCatalogSource = readFromGit(baselineRef, catalogPath);
  if (previousCatalogSource && catalog) {
    const previousCatalog = parseJson(catalogPath, previousCatalogSource);
    const currentContracts = new Set(
      catalog.events.map((event) => `${event.event_type}@${event.schema_version}`),
    );
    for (const previousEvent of previousCatalog?.events ?? []) {
      const key = `${previousEvent.event_type}@${previousEvent.schema_version}`;
      if (!currentContracts.has(key)) {
        fail(`catalog removed active contract ${key}; deprecate it instead`);
      }
    }
  }

  for (const { path: schemaPath, schema } of schemas.values()) {
    const previousSource = readFromGit(baselineRef, schemaPath);
    if (!previousSource) {
      continue;
    }
    const previousSchema = parseJson(schemaPath, previousSource);
    if (previousSchema) {
      compareCompatibility(
        previousSchema,
        schema,
        "$",
        relative(repositoryRoot, schemaPath),
      );
    }
  }
}

if (failures.length > 0) {
  console.error("Event contract validation failed:");
  for (const failure of failures) {
    console.error(`- ${failure}`);
  }
  process.exit(1);
}

console.log(
  `Event contract validation passed: ${catalog?.events?.length ?? 0} active contracts, ${schemas.size} schemas.`,
);
