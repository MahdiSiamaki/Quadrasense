import { describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { OpenApiDocument } from '@/lib/openapi'
import SchemaFields from '@/features/help/SchemaFields.vue'

/*
  A schema may hold itself - a tree whose nodes hold nodes. Each level opens on request, and a
  schema already open above is not offered again: the reference must end, whatever the document.
*/
const doc: OpenApiDocument = {
  openapi: '3.1.0',
  info: { title: 'test', version: '1' },
  paths: {},
  components: {
    schemas: {
      Node: {
        type: 'object',
        required: ['name'],
        properties: {
          name: { type: 'string', description: 'What the node is called.' },
          children: { type: 'array', items: { $ref: '#/components/schemas/Node' } },
          parent: { oneOf: [{ type: 'null' }, { $ref: '#/components/schemas/Node' }] },
        },
      },
    },
  },
}

describe('the fields of a schema', () => {
  it('opens a schema that holds itself once, and does not offer it again inside', async () => {
    const wrapper = mount(SchemaFields, { props: { doc, schema: { $ref: '#/components/schemas/Node' } }, attachTo: document.body })

    expect(wrapper.text()).toContain('What the node is called.')
    expect(wrapper.text()).toContain('array of Node')
    expect(wrapper.text()).toContain('Node or null')

    const children = wrapper.findAll('details').find((d) => d.find('summary').text() === 'Fields of Node')!
    ;(children.element as HTMLDetailsElement).open = true
    await children.trigger('toggle')
    await flushPromises()

    // The level inside lists Node's fields, and offers no way into Node again.
    const inner = children.find('ul')
    expect(inner.text()).toContain('What the node is called.')
    expect(inner.findAll('details')).toHaveLength(0)

    wrapper.unmount()
  })

  it('says so when an object has no fields', () => {
    const wrapper = mount(SchemaFields, { props: { doc, schema: { type: 'string' } } })

    expect(wrapper.text()).toBe('No fields.')
  })
})
