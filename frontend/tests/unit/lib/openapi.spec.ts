import { readFileSync } from 'node:fs'
import { resolve as resolvePath } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  curlOf,
  exampleOf,
  fieldsOf,
  routesOf,
  scopesOf,
  typeLabel,
  withoutNull,
  type OpenApiDocument,
} from '@/lib/openapi'

/*
  The reader the API reference is drawn from, held to the document the server really publishes:
  `docs/integration/openapi/pi-integration-v1.json` is the copy IntegrationOpenApiTests keeps equal
  to what the running server generates. So a construct the document starts to use and the reader
  does not know shows here, not on the page.
*/
const doc = JSON.parse(
  readFileSync(resolvePath(process.cwd(), '../docs/integration/openapi/pi-integration-v1.json'), 'utf-8'),
) as OpenApiDocument

const route = (method: string, path: string) => {
  const found = routesOf(doc).find((r) => r.method === method && r.path === path)
  if (!found) throw new Error(`no ${method} ${path} in the document`)
  return found
}

describe('reading the integration API document', () => {
  it('lists every route once, grouped in the order of the tags', () => {
    const routes = routesOf(doc)
    const operations = Object.values(doc.paths).flatMap((item) => Object.keys(item)).length

    expect(routes).toHaveLength(operations)
    expect(new Set(routes.map((r) => r.id)).size).toBe(routes.length)

    const tags = doc.tags!.map((t) => t.name)
    const ranks = routes.map((r) => tags.indexOf(r.tag))
    expect(ranks).toEqual([...ranks].sort((a, b) => a - b))
    expect(routes[0]).toMatchObject({ method: 'POST', path: '/api/integration/v1/oauth/token' })
  })

  it('says what each route asks of the token: none, any, or named scopes', () => {
    expect(scopesOf(route('POST', '/api/integration/v1/oauth/token').operation)).toBeNull()
    expect(scopesOf(route('GET', '/api/integration/v1/status').operation)).toEqual([])
    expect(scopesOf(route('POST', '/api/integration/v1/inventory/lookup').operation)).toEqual(['inventory.read'])
  })

  it('reads every property of every schema without losing its type', () => {
    for (const [name, schema] of Object.entries(doc.components!.schemas!)) {
      for (const field of fieldsOf(doc, schema)) {
        expect(field.type, `${name}.${field.name}`).not.toBe('any')
        expect(field.type, `${name}.${field.name}`).not.toContain('undefined')
      }
    }
  })

  it('says "or null" however 3.1 writes it', () => {
    const binding = fieldsOf(doc, { $ref: '#/components/schemas/InventoryBindingInfo' })
    const byName = Object.fromEntries(binding.map((f) => [f.name, f]))

    // A type list with "null" in it.
    expect(byName['tac']!.type).toBe('string or null')
    // A oneOf of null and a reference, the reference keeping the description written beside it.
    expect(byName['capabilities']!.type).toBe('HandsetCapabilitiesInfo or null')
    expect(byName['capabilities']!.description).toContain('What the model can do')
    expect(byName['capabilities']!.nestedName).toBe('HandsetCapabilitiesInfo')
    expect(byName['capabilities']!.nested).not.toBeNull()

    expect(byName['msisdn']).toMatchObject({ type: 'string', required: true })
  })

  it('names arrays by what they hold', () => {
    const lookup = fieldsOf(doc, { $ref: '#/components/schemas/InventoryLookupResponse' })
    const bindings = lookup.find((f) => f.name === 'bindings')!

    expect(bindings.type).toBe('array of InventoryBindingInfo')
    expect(bindings.nestedName).toBe('InventoryBindingInfo')
  })
})

describe('a request to start from', () => {
  it('fills every value with a placeholder, never an invented identifier', () => {
    const body = exampleOf(doc, { $ref: '#/components/schemas/IdentifierRequest' }) as Record<string, unknown>

    expect(Object.keys(body)).toContain('identifier')
    expect(JSON.stringify(body)).not.toMatch(/\d{6,}/)
  })

  it('asks for the token with HTTP Basic, the secret from a variable, not written into the command', () => {
    const curl = curlOf(doc, route('POST', '/api/integration/v1/oauth/token'))

    expect(curl.split(' \\\n')).toEqual([
      'curl -X POST "$PI/api/integration/v1/oauth/token"',
      '  -u "$CLIENT_ID:$CLIENT_SECRET"',
      '  --data-urlencode "grant_type=client_credentials"',
    ])
  })

  it('sends a lookup with the token and a JSON body', () => {
    const curl = curlOf(doc, route('POST', '/api/integration/v1/inventory/lookup'))

    expect(curl.split(' \\\n')).toEqual([
      'curl -X POST "$PI/api/integration/v1/inventory/lookup"',
      '  -H "Authorization: Bearer $TOKEN"',
      '  -H "Content-Type: application/json"',
      expect.stringMatching(/^ {2}-d '\{"identifier":"<string>"/),
    ])
  })

  it('puts a path parameter in as a placeholder', () => {
    expect(curlOf(doc, route('GET', '/api/integration/v1/devices/models/{tac}'))).toContain(
      '"$PI/api/integration/v1/devices/models/<tac>"',
    )
  })
})

describe('the small parts', () => {
  it('takes null off a type list and a oneOf alike', () => {
    expect(withoutNull({ type: ['null', 'integer'] })).toEqual({ schema: { type: 'integer' }, nullable: true })
    expect(withoutNull({ oneOf: [{ type: 'null' }, { $ref: '#/components/schemas/X' }] })).toEqual({
      schema: { $ref: '#/components/schemas/X', description: undefined },
      nullable: true,
    })
    expect(withoutNull({ type: 'string' })).toEqual({ schema: { type: 'string' }, nullable: false })
  })

  it('labels enums, formats and objects', () => {
    expect(typeLabel(doc, { type: 'string', enum: ['msisdn', 'imsi'] })).toBe('one of: msisdn, imsi')
    expect(typeLabel(doc, { type: 'integer', format: 'int64' })).toBe('integer (int64)')
    expect(typeLabel(doc, { type: ['null', 'string'], format: 'date' })).toBe('string (date) or null')
    expect(typeLabel(doc, { properties: { a: { type: 'string' } } })).toBe('object')
  })
})
