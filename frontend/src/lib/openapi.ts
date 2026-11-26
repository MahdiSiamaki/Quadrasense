/*
  Reading an OpenAPI 3.1 document for people: what each route takes, what it answers, and the
  shape of both, in words. The API reference (Help > API reference) is drawn from this.

  Only what the integration API's document uses is read. Two things 3.1 does that 3.0 did not
  matter here: "or null" is written as a type list (`["null", "string"]`) or as a `oneOf` with a
  `{ "type": "null" }` member, and a `$ref` may sit beside a description. Both are folded into one
  reading: the type, and whether it may be null.
*/

export interface OpenApiSchema {
  $ref?: string
  type?: string | string[]
  format?: string
  description?: string
  enum?: unknown[]
  items?: OpenApiSchema
  properties?: Record<string, OpenApiSchema>
  required?: string[]
  oneOf?: OpenApiSchema[]
  anyOf?: OpenApiSchema[]
  allOf?: OpenApiSchema[]
  additionalProperties?: boolean | OpenApiSchema
  default?: unknown
  pattern?: string
  minimum?: number
  maximum?: number
  minLength?: number
  maxLength?: number
  minItems?: number
  maxItems?: number
}

export interface OpenApiParameter {
  name: string
  in: 'path' | 'query' | 'header' | 'cookie'
  required?: boolean
  description?: string
  schema?: OpenApiSchema
}

export interface OpenApiMedia {
  schema?: OpenApiSchema
}

export interface OpenApiResponse {
  description: string
  content?: Record<string, OpenApiMedia>
}

export interface OpenApiOperation {
  tags?: string[]
  summary?: string
  description?: string
  operationId?: string
  parameters?: OpenApiParameter[]
  requestBody?: { content: Record<string, OpenApiMedia>; required?: boolean }
  responses?: Record<string, OpenApiResponse>
  security?: Array<Record<string, string[]>>
}

export interface OpenApiSecurityScheme {
  type: string
  description?: string
  flows?: { clientCredentials?: { tokenUrl: string; scopes: Record<string, string> } }
}

export interface OpenApiDocument {
  openapi: string
  info: { title: string; description?: string; version: string }
  paths: Record<string, Record<string, OpenApiOperation>>
  components?: {
    schemas?: Record<string, OpenApiSchema>
    securitySchemes?: Record<string, OpenApiSecurityScheme>
  }
  tags?: Array<{ name: string; description?: string }>
}

/** One route: its method and path, what it does, and the tag it is listed under. */
export interface Route {
  /** Stable, for an anchor: the operation id, or the method and path. */
  id: string
  method: string
  path: string
  tag: string
  operation: OpenApiOperation
}

const METHODS = ['get', 'post', 'put', 'patch', 'delete'] as const

/** `#/components/schemas/IdentifierRequest` → `IdentifierRequest`. */
export function refName(ref: string): string {
  return ref.slice(ref.lastIndexOf('/') + 1)
}

/** The schema a `$ref` points at, followed until it is not one; the schema itself otherwise. */
export function resolve(doc: OpenApiDocument, schema: OpenApiSchema | undefined): OpenApiSchema | undefined {
  let current = schema
  for (let hops = 0; current?.$ref && hops < 8; hops++) {
    current = doc.components?.schemas?.[refName(current.$ref)]
  }
  return current
}

/**
 * The schema with "or null" taken off, and whether it was there. A description written beside a
 * `$ref` (3.1 allows it) is kept on the result's `description`.
 */
export function withoutNull(schema: OpenApiSchema): { schema: OpenApiSchema; nullable: boolean } {
  if (Array.isArray(schema.type) && schema.type.includes('null')) {
    const rest = schema.type.filter((t) => t !== 'null')
    return { schema: { ...schema, type: rest.length === 1 ? rest[0] : rest }, nullable: true }
  }

  for (const key of ['oneOf', 'anyOf'] as const) {
    const members = schema[key]
    if (!members?.some((m) => m.type === 'null')) continue

    const rest = members.filter((m) => m.type !== 'null')
    if (rest.length === 1) {
      const inner = rest[0]!
      return {
        schema: { ...inner, description: inner.description ?? schema.description },
        nullable: true,
      }
    }
    return { schema: { ...schema, [key]: rest }, nullable: true }
  }

  return { schema, nullable: false }
}

/** "string", "integer (int64)", "array of InventoryBindingInfo", "one of: msisdn, imsi", each "or null" as written. */
export function typeLabel(doc: OpenApiDocument, schema: OpenApiSchema | undefined): string {
  if (!schema) return 'any'
  const { schema: plain, nullable } = withoutNull(schema)
  const orNull = nullable ? ' or null' : ''

  if (plain.$ref) return refName(plain.$ref) + orNull

  if (plain.enum?.length) {
    return 'one of: ' + plain.enum.map((v) => (v === null ? 'null' : String(v))).join(', ') + orNull
  }

  const alternatives = plain.oneOf ?? plain.anyOf
  if (alternatives?.length) return alternatives.map((a) => typeLabel(doc, a)).join(' or ') + orNull

  const type = Array.isArray(plain.type) ? plain.type.join(' or ') : plain.type
  if (type === 'array') return `array of ${typeLabel(doc, plain.items)}${orNull}`
  if (!type) return (plain.properties ? 'object' : 'any') + orNull

  return (plain.format ? `${type} (${plain.format})` : type) + orNull
}

/** One property of an object, as the reference lists it. */
export interface Field {
  name: string
  type: string
  required: boolean
  description: string | null
  /** An object below this one - the property itself, or the items of an array of them - to show on request. */
  nested: OpenApiSchema | null
  /** The named schema of what is nested, when it has a name. */
  nestedName: string | null
}

/** The object a schema describes, through references, "or null" and arrays: the thing with properties. */
export function objectOf(doc: OpenApiDocument, schema: OpenApiSchema | undefined): OpenApiSchema | null {
  let current = schema
  for (let hops = 0; current && hops < 8; hops++) {
    const plain = withoutNull(current).schema
    if (plain.$ref) {
      current = resolve(doc, plain)
      continue
    }
    if (plain.type === 'array') {
      current = plain.items
      continue
    }
    return plain.properties ? plain : null
  }
  return null
}

/** The properties of an object, in the order the document gives them. */
export function fieldsOf(doc: OpenApiDocument, schema: OpenApiSchema | undefined): Field[] {
  const object = objectOf(doc, schema)
  if (!object?.properties) return []

  const required = new Set(object.required ?? [])

  return Object.entries(object.properties).map(([name, property]) => {
    const plain = withoutNull(property).schema
    const target = plain.type === 'array' ? plain.items : plain
    const nested = objectOf(doc, property)
    const named = target && withoutNull(target).schema.$ref

    return {
      name,
      type: typeLabel(doc, property),
      required: required.has(name),
      description: plain.description ?? property.description ?? null,
      nested,
      nestedName: named ? refName(named) : null,
    }
  })
}

/** Every route, in the document's tag order, then in the order the document lists them. */
export function routesOf(doc: OpenApiDocument): Route[] {
  const order = (doc.tags ?? []).map((t) => t.name)
  const routes: Route[] = []

  for (const [path, item] of Object.entries(doc.paths)) {
    for (const method of METHODS) {
      const operation = item[method]
      if (!operation) continue
      routes.push({
        id: operation.operationId ?? `${method}-${path}`.replace(/[^A-Za-z0-9]+/g, '-'),
        method: method.toUpperCase(),
        path,
        tag: operation.tags?.[0] ?? 'Other',
        operation,
      })
    }
  }

  const rank = (tag: string) => (order.includes(tag) ? order.indexOf(tag) : order.length)
  return routes
    .map((route, index) => ({ route, index }))
    .sort((a, b) => rank(a.route.tag) - rank(b.route.tag) || a.index - b.index)
    .map(({ route }) => route)
}

/**
 * What a route asks of the caller's token: `null` when it takes none (the token endpoint itself),
 * `[]` when any registered system's token will do, else the scopes it needs.
 */
export function scopesOf(operation: OpenApiOperation): string[] | null {
  if (!operation.security?.length) return null
  return [...new Set(operation.security.flatMap((requirement) => Object.values(requirement).flat()))]
}

/**
 * A body to start from, built from the schema: each value a placeholder that says what goes there,
 * never an invented identifier. Required properties only when the object has any - the smallest
 * request that is accepted - and every property when it has none.
 */
export function exampleOf(doc: OpenApiDocument, schema: OpenApiSchema | undefined, depth = 0): unknown {
  if (!schema || depth > 4) return null
  const plain = withoutNull(schema).schema

  if (plain.$ref) return exampleOf(doc, resolve(doc, plain), depth + 1)
  if (plain.enum?.length) return plain.enum.find((v) => v !== null) ?? null
  if (plain.default !== undefined) return plain.default

  const alternatives = plain.oneOf ?? plain.anyOf
  if (alternatives?.length) return exampleOf(doc, alternatives[0], depth + 1)

  const type = Array.isArray(plain.type) ? plain.type[0] : plain.type
  switch (type) {
    case 'string':
      return plain.format === 'date' ? '<yyyy-MM-dd>' : plain.format === 'date-time' ? '<ISO 8601, UTC>' : '<string>'
    case 'integer':
    case 'number':
      return plain.minimum ?? 0
    case 'boolean':
      return true
    case 'array':
      return [exampleOf(doc, plain.items, depth + 1)]
    default: {
      if (!plain.properties) return {}
      const names = plain.required?.length ? plain.required : Object.keys(plain.properties)
      return Object.fromEntries(names.map((name) => [name, exampleOf(doc, plain.properties![name], depth + 1)]))
    }
  }
}

/** The media type and schema of a request body, JSON first. */
export function bodyOf(operation: OpenApiOperation): { mediaType: string; schema: OpenApiSchema | undefined } | null {
  const content = operation.requestBody?.content
  if (!content) return null
  const mediaType = 'application/json' in content ? 'application/json' : Object.keys(content)[0]
  return mediaType ? { mediaType, schema: content[mediaType]?.schema } : null
}

/**
 * The request as a shell command. `$PI` is the server's address and `$TOKEN` a bearer token from the
 * token route; placeholders in angle brackets are the caller's to fill. A secret is read from a
 * variable, never written into the command, where shell history would keep it.
 */
export function curlOf(doc: OpenApiDocument, route: Route): string {
  const path = route.path.replace(/\{([^}]+)\}/g, '<$1>')
  const required = (route.operation.parameters ?? []).filter((p) => p.in === 'query' && p.required)
  const query = required.length ? '?' + required.map((p) => `${p.name}=<${p.name}>`).join('&') : ''
  const lines = [`curl -X ${route.method} "$PI${path}${query}"`]

  const body = bodyOf(route.operation)
  const needsToken = scopesOf(route.operation) !== null

  if (needsToken) lines.push('  -H "Authorization: Bearer $TOKEN"')

  if (body?.mediaType === 'application/x-www-form-urlencoded') {
    // The token route: the system proves who it is with HTTP Basic, the way RFC 6749 (2.3.1)
    // prefers, and the form carries only what is required.
    if (!needsToken) lines.push('  -u "$CLIENT_ID:$CLIENT_SECRET"')
    const object = objectOf(doc, body.schema)
    for (const name of object?.required ?? []) {
      lines.push(`  --data-urlencode "${name}=${name === 'grant_type' ? 'client_credentials' : `<${name}>`}"`)
    }
  } else if (body) {
    lines.push(`  -H "Content-Type: ${body.mediaType}"`)
    lines.push(`  -d '${JSON.stringify(exampleOf(doc, body.schema))}'`)
  }

  return lines.join(' \\\n')
}
