#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "Core/gb.h"

#if defined(_WIN32)
#define CB_EXPORT __declspec(dllexport)
#else
#define CB_EXPORT __attribute__((visibility("default")))
#endif

typedef struct {
    uint8_t a, f, b, c, d, e, h, l;
    uint16_t sp, pc;
} cb_registers_t;

static GB_model_t map_model(int model)
{
    switch (model) {
        case 0: return GB_MODEL_DMG_B;
        case 1: return GB_MODEL_MGB;
        case 2: return GB_MODEL_CGB_0;
        case 3: return GB_MODEL_CGB_A;
        case 4: return GB_MODEL_CGB_B;
        case 5: return GB_MODEL_CGB_C;
        case 6: return GB_MODEL_CGB_D;
        case 7: return GB_MODEL_CGB_E;
        case 8: return GB_MODEL_AGB_A;
        case 9: return GB_MODEL_GBP_A;
        case 10: return GB_MODEL_SGB;
        case 11: return GB_MODEL_SGB2;
        default: return (GB_model_t)-1;
    }
}

CB_EXPORT const char *cb_oracle_baseline(void)
{
    return "SameBoy 1.0.3 213a12ce93d66b105a113debd9396306066a7cfc";
}

CB_EXPORT void *cb_oracle_create(int model, const uint8_t *rom, size_t rom_size)
{
    GB_model_t native_model = map_model(model);
    if ((int)native_model < 0 || rom == NULL || rom_size < 0x150) return NULL;

    GB_random_set_enabled(false);
    GB_gameboy_t *gb = GB_init(GB_alloc(), native_model);
    if (gb == NULL) return NULL;
    GB_load_rom_from_buffer(gb, rom, rom_size);

    /* Differential instruction tests begin at the documented post-boot CPU
       boundary. This does not claim to emulate execution of a boot ROM. */
    GB_write_memory(gb, 0xFF50, 1);
    GB_registers_t *registers = GB_get_registers(gb);
    registers->a = GB_is_cgb(gb) ? 0x11 : 0x01;
    registers->f = 0xB0;
    registers->b = 0x00;
    registers->c = 0x13;
    registers->d = 0x00;
    registers->e = 0xD8;
    registers->h = 0x01;
    registers->l = 0x4D;
    registers->sp = 0xFFFE;
    registers->pc = 0x0100;
    return gb;
}

CB_EXPORT void cb_oracle_destroy(void *instance)
{
    if (instance == NULL) return;
    GB_gameboy_t *gb = instance;
    GB_free(gb);
    GB_dealloc(gb);
}

CB_EXPORT int cb_oracle_model(void *instance)
{
    return instance == NULL ? -1 : GB_get_model(instance);
}

CB_EXPORT void cb_oracle_get_registers(void *instance, cb_registers_t *output)
{
    if (instance == NULL || output == NULL) return;
    GB_registers_t *r = GB_get_registers(instance);
    output->a = r->a; output->f = r->f;
    output->b = r->b; output->c = r->c;
    output->d = r->d; output->e = r->e;
    output->h = r->h; output->l = r->l;
    output->sp = r->sp; output->pc = r->pc;
}

CB_EXPORT uint8_t cb_oracle_read(void *instance, uint16_t address)
{
    return instance == NULL ? 0xFF : GB_read_memory(instance, address);
}

CB_EXPORT void cb_oracle_write(void *instance, uint16_t address, uint8_t value)
{
    if (instance != NULL) GB_write_memory(instance, address, value);
}

CB_EXPORT unsigned cb_oracle_step(void *instance)
{
    if (instance == NULL) return 0;
    /* SameBoy reports fixed 8 MHz ticks. Normal-speed T-cycles are half that. */
    return GB_run(instance) / 2;
}
