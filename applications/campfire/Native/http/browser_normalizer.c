/*
 * User-Agent recognition is adapted from 37signals' Campfire user_agent.rs
 * at pinned source revision 6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad
 * (useragent gem 0.16.11). See LICENSE.37signals for the MIT notice.
 */
#include "browser_normalizer.h"

#include <stdint.h>

typedef struct al_browser_product_v1 {
    uint32_t name_start;
    uint32_t name_length;
    uint32_t version_start;
    uint32_t version_length;
    uint32_t comment_start;
    uint32_t comment_length;
    uint32_t comment_count;
    uint32_t comment_present;
} al_browser_product_v1;

typedef struct al_browser_product_table_v1 {
    uint32_t count;
    al_browser_product_v1 products[AL_CAMPFIRE_BROWSER_MAX_PRODUCTS_V1];
} al_browser_product_table_v1;

typedef struct al_browser_scratch_v1 {
    al_browser_product_table_v1 products;
    uint16_t joined_text[AL_CAMPFIRE_BROWSER_MAX_INPUT_UNITS_V1];
} al_browser_scratch_v1;

_Static_assert(
    sizeof(al_browser_scratch_v1) == AL_CAMPFIRE_BROWSER_SCRATCH_BYTES_V1,
    "public browser scratch size must match the implementation");
_Static_assert(
    _Alignof(al_browser_scratch_v1) == AL_CAMPFIRE_BROWSER_SCRATCH_ALIGNMENT_BYTES_V1,
    "public browser scratch alignment must match the implementation");

typedef struct al_browser_span_v1 {
    uint32_t start;
    uint32_t length;
} al_browser_span_v1;

typedef enum al_browser_kind_v1 {
    AL_KIND_BASE,
    AL_KIND_EDGE,
    AL_KIND_INTERNET_EXPLORER,
    AL_KIND_OPERA,
    AL_KIND_WECHAT_BROWSER,
    AL_KIND_VIVALDI,
    AL_KIND_CHROME,
    AL_KIND_ITUNES,
    AL_KIND_PLAYSTATION,
    AL_KIND_PODCAST_ADDICT,
    AL_KIND_WEBKIT,
    AL_KIND_GECKO,
    AL_KIND_WINDOWS_MEDIA_PLAYER,
    AL_KIND_APPLE_CORE_MEDIA,
    AL_KIND_LIBAVFORMAT
} al_browser_kind_v1;

typedef struct al_browser_version_v1 {
    const uint16_t *units;
    uint32_t start;
    uint32_t length;
    const char *ascii;
    uint32_t ascii_length;
    uint32_t prefix_ios;
    uint32_t replace_underscores;
} al_browser_version_v1;

typedef struct al_browser_os_v1 {
    uint32_t present;
    uint32_t normalized_ios;
    al_browser_span_v1 comment;
    al_browser_span_v1 ios_version;
} al_browser_os_v1;

static const uint16_t al_default_user_agent_v1[] = {
    'M', 'o', 'z', 'i', 'l', 'l', 'a', '/', '4', '.', '0', ' ',
    '(', 'c', 'o', 'm', 'p', 'a', 't', 'i', 'b', 'l', 'e', ')'
};

static size_t al_ascii_length_v1(const char *value)
{
    size_t length = 0;
    while (value[length] != '\0') {
        ++length;
    }
    return length;
}

static uint32_t al_fold_code_unit_v1(uint16_t value)
{
    if (value >= (uint16_t)'A' && value <= (uint16_t)'Z') {
        return (uint32_t)value + ((uint32_t)'a' - (uint32_t)'A');
    }
    if (value == 0x212au) {
        return (uint32_t)'k';
    }
    return (uint32_t)value;
}

static int al_is_ruby_space_v1(uint16_t value)
{
    return value == (uint16_t)' ' || value == (uint16_t)'\t'
        || value == (uint16_t)'\n' || value == 0x000bu
        || value == 0x000cu || value == (uint16_t)'\r';
}

static int al_is_dotnet_space_v1(uint16_t value)
{
    return (value >= 0x0009u && value <= 0x000du)
        || value == 0x0020u || value == 0x0085u || value == 0x00a0u
        || value == 0x1680u || (value >= 0x2000u && value <= 0x200au)
        || value == 0x2028u || value == 0x2029u || value == 0x202fu
        || value == 0x205fu || value == 0x3000u;
}

static int al_is_product_character_v1(uint16_t value)
{
    return value != (uint16_t)'/' && !al_is_ruby_space_v1(value);
}

static int al_is_ascii_digit_v1(uint16_t value)
{
    return value >= (uint16_t)'0' && value <= (uint16_t)'9';
}

static int al_is_ascii_digit_or_dot_v1(uint16_t value)
{
    return al_is_ascii_digit_v1(value) || value == (uint16_t)'.';
}

static void al_ruby_strip_v1(
    const uint16_t *input,
    uint32_t *start,
    uint32_t *length)
{
    uint32_t begin = *start;
    uint32_t end = begin + *length;
    while (begin < end
           && (input[begin] == 0u || al_is_ruby_space_v1(input[begin]))) {
        ++begin;
    }
    while (end > begin
           && (input[end - 1u] == 0u || al_is_ruby_space_v1(input[end - 1u]))) {
        --end;
    }
    *start = begin;
    *length = end - begin;
}

static int al_span_equals_ascii_v1(
    const uint16_t *input,
    al_browser_span_v1 span,
    const char *ascii,
    int ignore_case)
{
    size_t index;
    size_t ascii_length = al_ascii_length_v1(ascii);
    if ((size_t)span.length != ascii_length) {
        return 0;
    }
    for (index = 0; index < ascii_length; ++index) {
        uint32_t left = (uint32_t)input[span.start + (uint32_t)index];
        uint32_t right = (uint32_t)(unsigned char)ascii[index];
        if (ignore_case) {
            left = al_fold_code_unit_v1((uint16_t)left);
            if (right >= (uint32_t)'A' && right <= (uint32_t)'Z') {
                right += (uint32_t)'a' - (uint32_t)'A';
            }
        }
        if (left != right) {
            return 0;
        }
    }
    return 1;
}

static size_t al_find_ascii_v1(
    const uint16_t *input,
    uint32_t start,
    uint32_t length,
    const char *ascii,
    uint32_t search_from,
    int ignore_case)
{
    size_t ascii_length = al_ascii_length_v1(ascii);
    uint32_t offset;
    if (ascii_length > (size_t)length || search_from > length) {
        return (size_t)-1;
    }
    for (offset = search_from; (size_t)offset + ascii_length <= (size_t)length; ++offset) {
        al_browser_span_v1 candidate;
        candidate.start = start + offset;
        candidate.length = (uint32_t)ascii_length;
        if (al_span_equals_ascii_v1(input, candidate, ascii, ignore_case)) {
            return (size_t)offset;
        }
    }
    return (size_t)-1;
}

static int al_span_contains_ascii_v1(
    const uint16_t *input,
    al_browser_span_v1 span,
    const char *ascii,
    int ignore_case)
{
    return al_find_ascii_v1(input, span.start, span.length, ascii, 0u, ignore_case)
        != (size_t)-1;
}

static uint32_t al_comment_count_v1(
    const uint16_t *input,
    uint32_t start,
    uint32_t length)
{
    uint32_t segment_start = 0;
    uint32_t segment_index = 0;
    uint32_t last_nonempty_count = 0;
    uint32_t index = 0;
    if (length == 0u) {
        return 0u;
    }
    while (index <= length) {
        if (index == length
            || (index + 1u < length
                && input[start + index] == (uint16_t)';'
                && input[start + index + 1u] == (uint16_t)' ')) {
            if (index > segment_start) {
                last_nonempty_count = segment_index + 1u;
            }
            ++segment_index;
            if (index == length) {
                break;
            }
            index += 2u;
            segment_start = index;
        } else {
            ++index;
        }
    }
    return last_nonempty_count;
}

static int al_comment_segment_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product,
    uint32_t wanted,
    al_browser_span_v1 *segment)
{
    uint32_t segment_start = 0;
    uint32_t segment_index = 0;
    uint32_t index = 0;
    if (wanted >= product->comment_count) {
        return 0;
    }
    while (index <= product->comment_length) {
        if (index == product->comment_length
            || (index + 1u < product->comment_length
                && input[product->comment_start + index] == (uint16_t)';'
                && input[product->comment_start + index + 1u] == (uint16_t)' ')) {
            if (segment_index == wanted) {
                segment->start = product->comment_start + segment_start;
                segment->length = index - segment_start;
                return 1;
            }
            ++segment_index;
            if (index == product->comment_length) {
                break;
            }
            index += 2u;
            segment_start = index;
        } else {
            ++index;
        }
    }
    return 0;
}

static uint32_t al_joined_comment_length_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product)
{
    uint32_t length;
    if (product->comment_count == 0u) {
        return 0u;
    }
    length = product->comment_length;
    while (length >= 2u
           && input[product->comment_start + length - 2u] == (uint16_t)';'
           && input[product->comment_start + length - 1u] == (uint16_t)' ') {
        length -= 2u;
    }
    return length;
}

static int al_match_product_v1(
    const uint16_t *input,
    uint32_t rest_start,
    uint32_t rest_length,
    al_browser_product_v1 *product,
    uint32_t *consumed)
{
    uint32_t quote_count = 0;
    uint32_t start;
    uint32_t index;
    uint32_t version_start;
    uint32_t comment_start;
    uint32_t close;
    int comment_condition;

    while (quote_count < rest_length
           && (input[rest_start + quote_count] == (uint16_t)'\''
               || input[rest_start + quote_count] == (uint16_t)'"')) {
        ++quote_count;
    }
    if (quote_count < rest_length
        && al_is_product_character_v1(input[rest_start + quote_count])) {
        start = quote_count;
    } else if (quote_count > 0u) {
        start = quote_count - 1u;
    } else {
        return 0;
    }

    index = start + 1u;
    while (index < rest_length
           && al_is_product_character_v1(input[rest_start + index])) {
        ++index;
    }
    product->name_start = rest_start + start;
    product->name_length = index - start;
    if (index < rest_length && input[rest_start + index] == (uint16_t)'/') {
        ++index;
    }
    version_start = index;
    while (index < rest_length
           && !al_is_ruby_space_v1(input[rest_start + index])
           && input[rest_start + index] != (uint16_t)',') {
        ++index;
    }
    product->version_start = rest_start + version_start;
    product->version_length = index - version_start;
    product->comment_start = 0u;
    product->comment_length = 0u;
    product->comment_count = 0u;
    product->comment_present = 0u;

    comment_condition = index < rest_length
        && al_is_ruby_space_v1(input[rest_start + index])
        && index + 1u < rest_length
        && input[rest_start + index + 1u] == (uint16_t)'(';
    if (comment_condition) {
        comment_start = index + 2u;
        close = comment_start;
        while (close < rest_length
               && input[rest_start + close] != (uint16_t)')') {
            ++close;
        }
        if (close < rest_length) {
            product->comment_start = rest_start + comment_start;
            product->comment_length = close - comment_start;
            product->comment_count = al_comment_count_v1(
                input,
                product->comment_start,
                product->comment_length);
            product->comment_present = 1u;
            index = close + 1u;
        }
    } else if (index + 10u <= rest_length
               && input[rest_start + index] == (uint16_t)','
               && input[rest_start + index + 1u] == (uint16_t)'g'
               && input[rest_start + index + 2u] == (uint16_t)'z'
               && input[rest_start + index + 3u] == (uint16_t)'i'
               && input[rest_start + index + 4u] == (uint16_t)'p'
               && input[rest_start + index + 5u] == (uint16_t)'('
               && input[rest_start + index + 6u] == (uint16_t)'g'
               && input[rest_start + index + 7u] == (uint16_t)'f'
               && input[rest_start + index + 8u] == (uint16_t)'e'
               && input[rest_start + index + 9u] == (uint16_t)')') {
        index += 10u;
    }
    *consumed = index;
    return 1;
}

static al_campfire_browser_status_v1 al_parse_products_v1(
    const uint16_t *input,
    uint32_t input_units,
    uint32_t present,
    al_browser_product_table_v1 *table,
    const uint16_t **effective_input)
{
    uint32_t rest_start = 0u;
    uint32_t rest_length = input_units;
    uint32_t consumed;
    al_browser_product_v1 product;
    const uint16_t *data = input;

    if (present == 0u) {
        data = al_default_user_agent_v1;
        rest_length = (uint32_t)(sizeof(al_default_user_agent_v1) / sizeof(al_default_user_agent_v1[0]));
    } else {
        uint32_t stripped_start = 0u;
        uint32_t stripped_length = input_units;
        if (stripped_length != 0u) {
            al_ruby_strip_v1(data, &stripped_start, &stripped_length);
        }
        if (stripped_length == 0u) {
            data = al_default_user_agent_v1;
            rest_length = (uint32_t)(sizeof(al_default_user_agent_v1) / sizeof(al_default_user_agent_v1[0]));
        }
    }

    table->count = 0u;
    while (rest_length > 0u) {
        if (!al_match_product_v1(data, rest_start, rest_length, &product, &consumed)) {
            break;
        }
        if (table->count >= AL_CAMPFIRE_BROWSER_MAX_PRODUCTS_V1) {
            return AL_CAMPFIRE_BROWSER_PRODUCT_LIMIT_V1;
        }
        table->products[table->count] = product;
        ++table->count;
        rest_start += consumed;
        rest_length -= consumed;
        al_ruby_strip_v1(data, &rest_start, &rest_length);
        if (consumed == 0u) {
            break;
        }
    }
    *effective_input = data;
    return AL_CAMPFIRE_BROWSER_OK_V1;
}

static int al_product_name_equals_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product,
    const char *name,
    int ignore_case)
{
    al_browser_span_v1 span;
    span.start = product->name_start;
    span.length = product->name_length;
    return al_span_equals_ascii_v1(input, span, name, ignore_case);
}

static int al_find_product_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    const char *name,
    int ignore_case)
{
    uint32_t index;
    for (index = 0u; index < table->count; ++index) {
        if (al_product_name_equals_v1(input, &table->products[index], name, ignore_case)) {
            return (int)index;
        }
    }
    return -1;
}

static int al_has_exact_product_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    const char *name)
{
    return al_find_product_v1(input, table, name, 0) >= 0;
}

static int al_has_product_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    const char *name)
{
    return al_find_product_v1(input, table, name, 1) >= 0;
}

static int al_comment_contains_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product,
    uint32_t index,
    const char *needle)
{
    al_browser_span_v1 segment;
    return al_comment_segment_v1(input, product, index, &segment)
        && al_span_contains_ascii_v1(input, segment, needle, 0);
}

static int al_joined_comment_contains_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product,
    const char *needle)
{
    al_browser_span_v1 joined;
    joined.start = product->comment_start;
    joined.length = al_joined_comment_length_v1(input, product);
    return al_span_contains_ascii_v1(input, joined, needle, 0);
}

static int al_trident_has_rv_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product)
{
    uint32_t comment_length = al_joined_comment_length_v1(input, product);
    uint32_t search_from = 0u;
    size_t trident;
    while ((trident = al_find_ascii_v1(
                input,
                product->comment_start,
                comment_length,
                "Trident",
                search_from,
                0)) != (size_t)-1) {
        uint32_t start = (uint32_t)trident + 7u;
        size_t line_end = al_find_ascii_v1(
            input,
            product->comment_start,
            comment_length,
            "\n",
            start,
            0);
        uint32_t finish = line_end == (size_t)-1 ? comment_length : (uint32_t)line_end;
        size_t rv;
        if (finish >= start) {
            uint32_t line_length = finish - start;
            rv = al_find_ascii_v1(
                input,
                product->comment_start + start,
                line_length,
                "rv:",
                0u,
                0);
            if (rv != (size_t)-1 && rv + start >= (size_t)start + 1u) {
                return 1;
            }
        }
        search_from = start;
    }
    return 0;
}

static int al_webkit_comment_version_v1(
    const uint16_t *input,
    al_browser_span_v1 comment,
    al_browser_span_v1 *version)
{
    static const char marker[] = "AppleWebKit";
    const uint32_t marker_length = (uint32_t)(sizeof(marker) - 1u);
    uint32_t length = 0u;
    uint32_t version_start;
    if (comment.length <= marker_length) {
        return 0;
    }
    {
        al_browser_span_v1 prefix;
        prefix.start = comment.start;
        prefix.length = marker_length;
        if (!al_span_equals_ascii_v1(input, prefix, marker, 1)
            || input[comment.start + marker_length] != (uint16_t)'/') {
            return 0;
        }
    }
    version_start = comment.start + marker_length + 1u;
    while (length < comment.length - marker_length - 1u
           && al_is_ascii_digit_or_dot_v1(input[version_start + length])) {
        ++length;
    }
    if (length == 0u) {
        return 0;
    }
    version->start = version_start;
    version->length = length;
    return 1;
}

static int al_has_webkit_comment_version_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    uint32_t product_index;
    for (product_index = 0u; product_index < table->count; ++product_index) {
        const al_browser_product_v1 *product = &table->products[product_index];
        uint32_t comment_index;
        for (comment_index = 0u; comment_index < product->comment_count; ++comment_index) {
            al_browser_span_v1 comment;
            al_browser_span_v1 version;
            if (al_comment_segment_v1(input, product, comment_index, &comment)
                && al_webkit_comment_version_v1(input, comment, &version)) {
                return 1;
            }
        }
    }
    return 0;
}

static int al_is_kind_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    al_browser_kind_v1 kind)
{
    const al_browser_product_v1 *first = table->count == 0u ? NULL : &table->products[0];
    const al_browser_product_v1 *last = table->count == 0u ? NULL : &table->products[table->count - 1u];
    uint32_t index;
    switch (kind) {
    case AL_KIND_BASE:
        return 1;
    case AL_KIND_EDGE:
        return last != NULL && al_product_name_equals_v1(input, last, "Edge", 0);
    case AL_KIND_INTERNET_EXPLORER:
        return first != NULL
            && first->comment_present != 0u
            && (al_comment_contains_v1(input, first, 1u, "MSIE")
                || al_trident_has_rv_v1(input, first));
    case AL_KIND_OPERA:
        return (first != NULL && al_product_name_equals_v1(input, first, "Opera", 0))
            || (last != NULL && al_product_name_equals_v1(input, last, "OPR", 0));
    case AL_KIND_WECHAT_BROWSER:
        for (index = 0u; index < table->count; ++index) {
            al_browser_span_v1 name;
            name.start = table->products[index].name_start;
            name.length = table->products[index].name_length;
            if (al_span_contains_ascii_v1(input, name, "micromessenger", 1)) {
                return 1;
            }
        }
        return 0;
    case AL_KIND_VIVALDI:
        return al_has_exact_product_v1(input, table, "Vivaldi");
    case AL_KIND_CHROME:
        return al_has_exact_product_v1(input, table, "Chrome")
            || al_has_exact_product_v1(input, table, "CriOS");
    case AL_KIND_ITUNES:
        return al_has_exact_product_v1(input, table, "iTunes");
    case AL_KIND_PLAYSTATION:
        return first != NULL
            && (al_comment_contains_v1(input, first, 0u, "PLAYSTATION 3")
                || al_comment_contains_v1(input, first, 0u, "PlayStation Vita")
                || al_comment_contains_v1(input, first, 0u, "PlayStation 4"));
    case AL_KIND_PODCAST_ADDICT:
        return table->count >= 3u
            && al_product_name_equals_v1(input, &table->products[0], "Podcast", 0)
            && al_product_name_equals_v1(input, &table->products[1], "Addict", 0)
            && al_product_name_equals_v1(input, &table->products[2], "-", 0);
    case AL_KIND_WEBKIT:
        return al_has_product_v1(input, table, "AppleWebKit")
            || al_has_webkit_comment_version_v1(input, table);
    case AL_KIND_GECKO:
        return first != NULL && al_product_name_equals_v1(input, first, "Mozilla", 0);
    case AL_KIND_WINDOWS_MEDIA_PLAYER:
        for (index = 0u; index < table->count; ++index) {
            const al_browser_product_v1 *product = &table->products[index];
            if ((al_product_name_equals_v1(input, product, "NSPlayer", 0)
                 || al_product_name_equals_v1(input, product, "Windows-Media-Player", 0)
                 || al_product_name_equals_v1(input, product, "WMFSDK", 0))
                && first != NULL
                && !al_span_equals_ascii_v1(input,
                    (al_browser_span_v1){ first->version_start, first->version_length },
                    "4.1.0.3856", 0)
                && !al_span_equals_ascii_v1(input,
                    (al_browser_span_v1){ first->version_start, first->version_length },
                    "7.10.0.3059", 0)
                && !al_span_equals_ascii_v1(input,
                    (al_browser_span_v1){ first->version_start, first->version_length },
                    "7.0.0.1956", 0)) {
                return 1;
            }
        }
        return 0;
    case AL_KIND_APPLE_CORE_MEDIA:
        return al_has_exact_product_v1(input, table, "AppleCoreMedia");
    case AL_KIND_LIBAVFORMAT:
        return al_has_exact_product_v1(input, table, "Lavf")
            || (al_has_exact_product_v1(input, table, "NSPlayer")
                && first != NULL
                && al_span_equals_ascii_v1(input,
                    (al_browser_span_v1){ first->version_start, first->version_length },
                    "4.1.0.3856", 0));
    }
    return 0;
}

static al_browser_kind_v1 al_browser_kind_v1_for_products(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    static const al_browser_kind_v1 priority[] = {
        AL_KIND_EDGE,
        AL_KIND_INTERNET_EXPLORER,
        AL_KIND_OPERA,
        AL_KIND_WECHAT_BROWSER,
        AL_KIND_VIVALDI,
        AL_KIND_CHROME,
        AL_KIND_ITUNES,
        AL_KIND_PLAYSTATION,
        AL_KIND_PODCAST_ADDICT,
        AL_KIND_WEBKIT,
        AL_KIND_GECKO,
        AL_KIND_WINDOWS_MEDIA_PLAYER,
        AL_KIND_APPLE_CORE_MEDIA,
        AL_KIND_LIBAVFORMAT
    };
    size_t index;
    for (index = 0; index < sizeof(priority) / sizeof(priority[0]); ++index) {
        if (al_is_kind_v1(input, table, priority[index])) {
            return priority[index];
        }
    }
    return AL_KIND_BASE;
}

static int al_kind_uses_commented_application_v1(al_browser_kind_v1 kind)
{
    return kind == AL_KIND_CHROME || kind == AL_KIND_VIVALDI
        || kind == AL_KIND_WEBKIT || kind == AL_KIND_ITUNES
        || kind == AL_KIND_APPLE_CORE_MEDIA;
}

static int al_application_index_v1(
    const al_browser_product_table_v1 *table,
    al_browser_kind_v1 kind)
{
    uint32_t index;
    if (!al_kind_uses_commented_application_v1(kind)) {
        return table->count == 0u ? -1 : 0;
    }
    for (index = 0u; index < table->count; ++index) {
        if (table->products[index].comment_count > 0u) {
            return (int)index;
        }
    }
    return -1;
}

static int al_playstation_browser_is_silk_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    const al_browser_product_v1 *first = table->count == 0u ? NULL : &table->products[0];
    if (first != NULL && al_comment_contains_v1(input, first, 0u, "PLAYSTATION 3")) {
        return 0;
    }
    return table->count > 0u
        && al_product_name_equals_v1(input, &table->products[table->count - 1u], "Silk", 0);
}

static uint32_t al_join_comment_segments_with_spaces_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product,
    uint16_t *output)
{
    uint32_t comment_index;
    uint32_t output_length = 0u;
    for (comment_index = 0u; comment_index < product->comment_count; ++comment_index) {
        al_browser_span_v1 segment;
        uint32_t unit;
        if (!al_comment_segment_v1(input, product, comment_index, &segment)) {
            continue;
        }
        if (comment_index > 0u) {
            output[output_length++] = (uint16_t)' ';
        }
        for (unit = 0u; unit < segment.length; ++unit) {
            output[output_length++] = input[segment.start + unit];
        }
    }
    return output_length;
}

static int al_webkit_platform_is_blackberry_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    int app = al_application_index_v1(table, AL_KIND_WEBKIT);
    al_browser_span_v1 first_comment;
    const al_browser_product_v1 *product;
    uint32_t index;
    if (app < 0
        || !al_comment_segment_v1(input, &table->products[(uint32_t)app], 0u, &first_comment)) {
        return 0;
    }
    if (al_span_equals_ascii_v1(input, first_comment, "BB10", 0)) {
        return 1;
    }
    product = &table->products[(uint32_t)app];
    for (index = 0u; index < product->comment_count; ++index) {
        al_browser_span_v1 comment;
        if (al_comment_segment_v1(input, product, index, &comment)
            && al_span_contains_ascii_v1(input, comment, "Android", 0)) {
            return 0;
        }
    }
    return al_span_equals_ascii_v1(input, first_comment, "BlackBerry", 0);
}

static int al_ios_version_v1(
    const uint16_t *input,
    al_browser_span_v1 comment,
    al_browser_span_v1 *version)
{
    uint32_t search_from = 0u;
    size_t cpu;
    while ((cpu = al_find_ascii_v1(input, comment.start, comment.length, "CPU ", search_from, 0))
           != (size_t)-1) {
        uint32_t after_device = (uint32_t)cpu + 4u;
        size_t found;
        if (al_find_ascii_v1(input, comment.start, comment.length, "iPhone ", after_device, 0)
            == (size_t)after_device) {
            after_device += 7u;
        } else if (al_find_ascii_v1(input, comment.start, comment.length, "iPod ", after_device, 0)
                   == (size_t)after_device) {
            after_device += 5u;
        }
        found = al_find_ascii_v1(input, comment.start, comment.length, "OS ", after_device, 0);
        if (found == (size_t)after_device) {
            uint32_t version_start = (uint32_t)found + 3u;
            uint32_t length = 0u;
            while (version_start + length < comment.length
                   && (al_is_ascii_digit_v1(input[comment.start + version_start + length])
                       || input[comment.start + version_start + length] == (uint16_t)'_')) {
                ++length;
            }
            if (length > 0u
                && al_find_ascii_v1(
                    input,
                    comment.start,
                    comment.length,
                    " like Mac OS X",
                    version_start + length,
                    0) == (size_t)(version_start + length)) {
                version->start = comment.start + version_start;
                version->length = length;
                return 1;
            }
        }
        search_from = (uint32_t)cpu + 4u;
    }
    return 0;
}

static al_browser_os_v1 al_webkit_os_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    al_browser_os_v1 result;
    int app = al_application_index_v1(table, AL_KIND_WEBKIT);
    const al_browser_product_v1 *product;
    al_browser_span_v1 first;
    al_browser_span_v1 second;
    al_browser_span_v1 third;
    uint32_t has_first;
    uint32_t has_second;
    uint32_t has_third;
    uint32_t index;
    result.present = 0u;
    result.normalized_ios = 0u;
    result.comment.start = 0u;
    result.comment.length = 0u;
    result.ios_version.start = 0u;
    result.ios_version.length = 0u;
    if (app < 0) {
        return result;
    }
    product = &table->products[(uint32_t)app];
    has_first = al_comment_segment_v1(input, product, 0u, &first) ? 1u : 0u;
    has_second = al_comment_segment_v1(input, product, 1u, &second) ? 1u : 0u;
    has_third = al_comment_segment_v1(input, product, 2u, &third) ? 1u : 0u;
    if (!has_first) {
        return result;
    }
    if (al_span_contains_ascii_v1(input, first, "Windows NT", 0)) {
        result.present = 1u;
        result.comment = first;
    } else if (!has_third || (has_second && al_span_contains_ascii_v1(input, second, "Android", 0))) {
        if (!has_second) {
            return result;
        }
        result.present = 1u;
        result.comment = second;
    } else {
        for (index = 0u; index < product->comment_count; ++index) {
            al_browser_span_v1 comment;
            if (al_comment_segment_v1(input, product, index, &comment)
                && al_ios_version_v1(input, comment, &result.ios_version)) {
                result.present = 1u;
                result.normalized_ios = 1u;
                result.comment = comment;
                return result;
            }
        }
        if (!has_third) {
            return result;
        }
        result.present = 1u;
        result.comment = third;
    }
    if (result.present != 0u) {
        result.normalized_ios = al_ios_version_v1(input, result.comment, &result.ios_version) ? 1u : 0u;
    }
    return result;
}

static int al_webkit_browser_is_android_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    al_browser_os_v1 os = al_webkit_os_v1(input, table);
    return os.present != 0u && os.normalized_ios == 0u
        && al_span_contains_ascii_v1(input, os.comment, "Android", 0);
}

static int al_webkit_browser_is_safari_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    if (al_webkit_browser_is_android_v1(input, table)) {
        return 0;
    }
    if (al_webkit_platform_is_blackberry_v1(input, table)) {
        return 0;
    }
    return 1;
}

static int al_gecko_browser_index_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    static const char *const names[] = {
        "PaleMoon", "Firefox", "Camino", "Iceweasel", "Seamonkey"
    };
    size_t index;
    int product;
    for (index = 0; index < sizeof(names) / sizeof(names[0]); ++index) {
        product = al_find_product_v1(input, table, names[index], 1);
        if (product >= 0) {
            return product;
        }
    }
    return table->count == 0u ? -1 : 0;
}

static int al_browser_family_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    al_browser_kind_v1 kind)
{
    int app;
    switch (kind) {
    case AL_KIND_WEBKIT:
        return al_webkit_browser_is_safari_v1(input, table)
            ? AL_CAMPFIRE_BROWSER_SAFARI_V1
            : AL_CAMPFIRE_BROWSER_OTHER_V1;
    case AL_KIND_INTERNET_EXPLORER:
        return AL_CAMPFIRE_BROWSER_INTERNET_EXPLORER_V1;
    case AL_KIND_OPERA:
        return AL_CAMPFIRE_BROWSER_OPERA_V1;
    case AL_KIND_CHROME:
        return al_has_product_v1(input, table, "Iron")
            ? AL_CAMPFIRE_BROWSER_OTHER_V1
            : AL_CAMPFIRE_BROWSER_CHROME_V1;
    case AL_KIND_GECKO:
        app = al_gecko_browser_index_v1(input, table);
        return app >= 0
            && al_product_name_equals_v1(input, &table->products[(uint32_t)app], "Firefox", 1)
            ? AL_CAMPFIRE_BROWSER_FIREFOX_V1
            : AL_CAMPFIRE_BROWSER_OTHER_V1;
    case AL_KIND_BASE:
        app = al_application_index_v1(table, kind);
        if (app >= 0
            && al_product_name_equals_v1(input, &table->products[(uint32_t)app], "Safari", 1)) {
            return AL_CAMPFIRE_BROWSER_SAFARI_V1;
        }
        if (app >= 0
            && al_product_name_equals_v1(input, &table->products[(uint32_t)app], "Chrome", 1)) {
            return AL_CAMPFIRE_BROWSER_CHROME_V1;
        }
        if (app >= 0
            && al_product_name_equals_v1(input, &table->products[(uint32_t)app], "Firefox", 1)) {
            return AL_CAMPFIRE_BROWSER_FIREFOX_V1;
        }
        if (app >= 0
            && al_product_name_equals_v1(input, &table->products[(uint32_t)app], "Opera", 1)) {
            return AL_CAMPFIRE_BROWSER_OPERA_V1;
        }
        if (app >= 0
            && al_product_name_equals_v1(input, &table->products[(uint32_t)app], "Internet Explorer", 1)) {
            return AL_CAMPFIRE_BROWSER_INTERNET_EXPLORER_V1;
        }
        return AL_CAMPFIRE_BROWSER_OTHER_V1;
    default:
        return AL_CAMPFIRE_BROWSER_OTHER_V1;
    }
}

static al_browser_version_v1 al_empty_version_v1(void)
{
    al_browser_version_v1 result;
    result.units = NULL;
    result.start = 0u;
    result.length = 0u;
    result.ascii = NULL;
    result.ascii_length = 0u;
    result.prefix_ios = 0u;
    result.replace_underscores = 0u;
    return result;
}

static al_browser_version_v1 al_input_version_v1(
    const uint16_t *input,
    uint32_t start,
    uint32_t length)
{
    al_browser_version_v1 result = al_empty_version_v1();
    result.units = input;
    result.start = start;
    result.length = length;
    return result;
}

static al_browser_version_v1 al_ascii_version_v1(const char *value)
{
    al_browser_version_v1 result = al_empty_version_v1();
    result.ascii = value;
    result.ascii_length = (uint32_t)al_ascii_length_v1(value);
    return result;
}

static al_browser_version_v1 al_ios_version_only_v1(
    const uint16_t *input,
    al_browser_span_v1 ios)
{
    al_browser_version_v1 result = al_input_version_v1(input, ios.start, ios.length);
    result.replace_underscores = 1u;
    return result;
}

static int al_version_is_blank_v1(al_browser_version_v1 version)
{
    uint32_t index;
    uint32_t length = version.length + (version.prefix_ios != 0u ? 4u : 0u)
        + version.ascii_length;
    if (length == 0u) {
        return 1;
    }
    if (version.prefix_ios != 0u || (version.ascii != NULL && version.ascii_length > 0u)) {
        return 0;
    }
    for (index = 0u; index < version.length; ++index) {
        if (!al_is_dotnet_space_v1(version.units[version.start + index])) {
            return 0;
        }
    }
    return 1;
}

static int al_version_is_all_ruby_space_v1(al_browser_version_v1 version)
{
    uint32_t index;
    if (version.ascii != NULL || version.prefix_ios != 0u) {
        return version.length == 0u && version.ascii_length == 0u;
    }
    for (index = 0u; index < version.length; ++index) {
        if (!al_is_ruby_space_v1(version.units[version.start + index])) {
            return 0;
        }
    }
    return 1;
}

static int al_capture_after_v1(
    const uint16_t *input,
    al_browser_span_v1 value,
    const char *marker,
    int (*predicate)(uint16_t),
    al_browser_span_v1 *captured)
{
    uint32_t search_from = 0u;
    size_t found;
    while ((found = al_find_ascii_v1(
                input,
                value.start,
                value.length,
                marker,
                search_from,
                0)) != (size_t)-1) {
        uint32_t start = (uint32_t)found + (uint32_t)al_ascii_length_v1(marker);
        uint32_t length = 0u;
        while (start + length < value.length
               && predicate(input[value.start + start + length])) {
            ++length;
        }
        if (length > 0u) {
            captured->start = value.start + start;
            captured->length = length;
            return 1;
        }
        search_from = start;
    }
    return 0;
}

static int al_capture_digits_dot_v1(uint16_t value)
{
    return al_is_ascii_digit_or_dot_v1(value);
}

static int al_opera_mini_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table)
{
    return table->count > 0u
        && al_joined_comment_contains_v1(input, &table->products[0], "Opera Mini");
}

static int al_ie_version_v1(
    const uint16_t *input,
    al_browser_span_v1 comment,
    al_browser_span_v1 *version)
{
    uint32_t search_from = 0u;
    while (search_from < comment.length) {
        size_t msie = al_find_ascii_v1(input, comment.start, comment.length, "MSIE", search_from, 0);
        size_t rv = al_find_ascii_v1(input, comment.start, comment.length, "rv:", search_from, 0);
        int use_msie = msie != (size_t)-1 && (rv == (size_t)-1 || msie < rv);
        if (use_msie) {
            uint32_t after_marker = (uint32_t)msie + 4u;
            if (after_marker < comment.length
                && al_is_ruby_space_v1(input[comment.start + after_marker])) {
                uint32_t version_start = after_marker + 1u;
                uint32_t length = 0u;
                while (version_start + length < comment.length
                       && (al_is_ascii_digit_v1(input[comment.start + version_start + length])
                           || input[comment.start + version_start + length] == (uint16_t)'.')) {
                    ++length;
                }
                if (length > 0u) {
                    version->start = comment.start + version_start;
                    version->length = length;
                    return 1;
                }
            }
            search_from = after_marker;
        } else if (rv != (size_t)-1) {
            uint32_t version_start = (uint32_t)rv + 3u;
            uint32_t length = 0u;
            while (version_start + length < comment.length
                   && (al_is_ascii_digit_v1(input[comment.start + version_start + length])
                       || input[comment.start + version_start + length] == (uint16_t)'.')) {
                ++length;
            }
            if (length > 0u) {
                version->start = comment.start + version_start;
                version->length = length;
                return 1;
            }
            search_from = version_start;
        } else {
            search_from = comment.length;
        }
    }
    return 0;
}

static int al_find_webkit_comment_version_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    al_browser_span_v1 *version)
{
    uint32_t product_index;
    for (product_index = 0u; product_index < table->count; ++product_index) {
        const al_browser_product_v1 *product = &table->products[product_index];
        uint32_t comment_index;
        for (comment_index = 0u; comment_index < product->comment_count; ++comment_index) {
            al_browser_span_v1 comment;
            if (al_comment_segment_v1(input, product, comment_index, &comment)
                && al_webkit_comment_version_v1(input, comment, version)) {
                return 1;
            }
        }
    }
    return 0;
}

static int al_webkit_build_version_v1(
    const uint16_t *input,
    al_browser_span_v1 build,
    const char **mapped)
{
    static const struct {
        const char *build;
        const char *version;
    } mappings[] = {
        { "85.7", "1.0" },
        { "85.8.5", "1.0.3" }, { "85.8.2", "1.0.3" },
        { "124", "1.2" }, { "125.2", "1.2.2" }, { "125.4", "1.2.3" },
        { "125.5.5", "1.2.4" }, { "125.5.6", "1.2.4" }, { "125.5.7", "1.2.4" },
        { "312.1.1", "1.3" }, { "312.1", "1.3" },
        { "312.5", "1.3.1" }, { "312.5.1", "1.3.1" }, { "312.5.2", "1.3.1" },
        { "312.8", "1.3.2" }, { "312.8.1", "1.3.2" },
        { "412", "2.0" }, { "412.6", "2.0" }, { "412.6.2", "2.0" },
        { "412.7", "2.0.1" }, { "416.11", "2.0.2" }, { "416.12", "2.0.2" },
        { "417.9", "2.0.3" }, { "418", "2.0.3" },
        { "418.8", "2.0.4" }, { "418.9", "2.0.4" },
        { "418.9.1", "2.0.4" }, { "419", "2.0.4" },
        { "425.13", "2.2" }, { "534.52.7", "5.1.2" }
    };
    size_t index;
    for (index = 0; index < sizeof(mappings) / sizeof(mappings[0]); ++index) {
        if (al_span_equals_ascii_v1(input, build, mappings[index].build, 0)) {
            *mapped = mappings[index].version;
            return 1;
        }
    }
    return 0;
}

static al_browser_version_v1 al_compute_version_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    al_browser_kind_v1 kind,
    uint16_t *workspace)
{
    int application = al_application_index_v1(table, kind);
    const al_browser_product_v1 *first = table->count == 0u ? NULL : &table->products[0];
    const al_browser_product_v1 *last = table->count == 0u ? NULL : &table->products[table->count - 1u];
    int product;
    al_browser_span_v1 comment;
    al_browser_span_v1 version;
    al_browser_version_v1 result = al_empty_version_v1();

    switch (kind) {
    case AL_KIND_BASE:
    case AL_KIND_WINDOWS_MEDIA_PLAYER:
    case AL_KIND_APPLE_CORE_MEDIA:
        return application < 0
            ? result
            : al_input_version_v1(input,
                table->products[(uint32_t)application].version_start,
                table->products[(uint32_t)application].version_length);
    case AL_KIND_EDGE:
    case AL_KIND_VIVALDI:
        return last == NULL ? result : al_input_version_v1(input, last->version_start, last->version_length);
    case AL_KIND_INTERNET_EXPLORER:
        if (application < 0) {
            return result;
        }
        comment.start = table->products[(uint32_t)application].comment_start;
        comment.length = al_joined_comment_length_v1(input, &table->products[(uint32_t)application]);
        if (al_ie_version_v1(input, comment, &version)) {
            return al_input_version_v1(input, version.start, version.length);
        }
        return result;
    case AL_KIND_OPERA:
        if (al_opera_mini_v1(input, table)) {
            if (application >= 0) {
                const al_browser_product_v1 *app = &table->products[(uint32_t)application];
                uint32_t comment_index;
                for (comment_index = 0u; comment_index < app->comment_count; ++comment_index) {
                    al_browser_span_v1 segment;
                    if (al_comment_segment_v1(input, app, comment_index, &segment)
                        && al_capture_after_v1(input, segment, "Opera Mini/", al_capture_digits_dot_v1, &version)) {
                        return al_input_version_v1(input, version.start, version.length);
                    }
                }
            }
            return al_ascii_version_v1("");
        }
        product = al_find_product_v1(input, table, "Version", 1);
        if (product < 0) product = al_find_product_v1(input, table, "OPR", 1);
        if (product >= 0) {
            return al_input_version_v1(input,
                table->products[(uint32_t)product].version_start,
                table->products[(uint32_t)product].version_length);
        }
        return application < 0 ? result : al_input_version_v1(input,
            table->products[(uint32_t)application].version_start,
            table->products[(uint32_t)application].version_length);
    case AL_KIND_WECHAT_BROWSER:
        product = al_find_product_v1(input, table, "MicroMessenger", 1);
        return product < 0 ? result : al_input_version_v1(input,
            table->products[(uint32_t)product].version_start,
            table->products[(uint32_t)product].version_length);
    case AL_KIND_CHROME:
        product = al_find_product_v1(input, table, "CriOs", 1);
        if (product < 0) product = al_find_product_v1(input, table, "Chrome", 1);
        return product < 0 ? result : al_input_version_v1(input,
            table->products[(uint32_t)product].version_start,
            table->products[(uint32_t)product].version_length);
    case AL_KIND_ITUNES:
        product = al_find_product_v1(input, table, "iTunes", 1);
        return product < 0 ? result : al_input_version_v1(input,
            table->products[(uint32_t)product].version_start,
            table->products[(uint32_t)product].version_length);
    case AL_KIND_PLAYSTATION:
        if (al_playstation_browser_is_silk_v1(input, table)) {
            return last == NULL ? result : al_input_version_v1(input, last->version_start, last->version_length);
        }
        if (application < 0) {
            return result;
        }
        comment.start = 0u;
        comment.length = al_join_comment_segments_with_spaces_v1(
            input,
            &table->products[(uint32_t)application],
            workspace);
        {
            al_browser_span_v1 joined;
            const char *markers[] = { "PLAYSTATION 3 ", "PlayStation 4 ", "PlayStation Vita " };
            size_t marker_index;
            const char *marker = NULL;
            joined.start = 0u;
            joined.length = comment.length;
            for (marker_index = 0; marker_index < sizeof(markers) / sizeof(markers[0]); ++marker_index) {
                if (al_span_contains_ascii_v1(workspace, joined, markers[marker_index], 0)) {
                    marker = markers[marker_index];
                    break;
                }
            }
            if (marker != NULL) {
                uint32_t segment_start = 0u;
                uint32_t last_nonempty_start = 0u;
                uint32_t last_nonempty_length = 0u;
                uint32_t marker_length = (uint32_t)al_ascii_length_v1(marker);
                size_t found;
                uint32_t search = 0u;
                while ((found = al_find_ascii_v1(workspace, joined.start, joined.length, marker, search, 0))
                       != (size_t)-1) {
                    if ((uint32_t)found > segment_start) {
                        last_nonempty_start = segment_start;
                        last_nonempty_length = (uint32_t)found - segment_start;
                    }
                    segment_start = (uint32_t)found + marker_length;
                    search = segment_start;
                }
                if (segment_start < comment.length) {
                    last_nonempty_start = segment_start;
                    last_nonempty_length = comment.length - segment_start;
                }
                return al_input_version_v1(workspace,
                    last_nonempty_start,
                    last_nonempty_length);
            }
        }
        return al_ascii_version_v1("");
    case AL_KIND_PODCAST_ADDICT:
        return result;
    case AL_KIND_WEBKIT:
        product = al_find_product_v1(input, table, "Version", 1);
        if (product >= 0) {
            return al_input_version_v1(input,
                table->products[(uint32_t)product].version_start,
                table->products[(uint32_t)product].version_length);
        }
        {
            al_browser_os_v1 os = al_webkit_os_v1(input, table);
            if (os.present != 0u && al_webkit_browser_is_safari_v1(input, table)) {
                if (os.normalized_ios != 0u) {
                    return al_ios_version_only_v1(input, os.ios_version);
                }
                if (al_capture_after_v1(input, os.comment, "iOS ", al_capture_digits_dot_v1, &version)) {
                    return al_input_version_v1(input, version.start, version.length);
                }
            }
        }
        product = al_find_product_v1(input, table, "AppleWebKit", 1);
        if (product >= 0) {
            version.start = table->products[(uint32_t)product].version_start;
            version.length = table->products[(uint32_t)product].version_length;
        } else if (!al_find_webkit_comment_version_v1(input, table, &version)) {
            return al_ascii_version_v1("");
        }
        {
            const char *mapped = NULL;
            if (al_webkit_build_version_v1(input, version, &mapped)) {
                return al_ascii_version_v1(mapped);
            }
        }
        return al_ascii_version_v1("");
    case AL_KIND_GECKO:
        product = al_gecko_browser_index_v1(input, table);
        if (product >= 0) {
            result = al_input_version_v1(input,
                table->products[(uint32_t)product].version_start,
                table->products[(uint32_t)product].version_length);
            if (!al_version_is_all_ruby_space_v1(result)) {
                return result;
            }
        }
        return first == NULL ? al_empty_version_v1()
            : al_input_version_v1(input, first->version_start, first->version_length);
    case AL_KIND_LIBAVFORMAT:
        if (al_has_product_v1(input, table, "NSPlayer")) {
            return result;
        }
        return application < 0 ? result : al_input_version_v1(input,
            table->products[(uint32_t)application].version_start,
            table->products[(uint32_t)application].version_length);
    }
    return result;
}

static int al_application_name_contains_bot_v1(
    const uint16_t *input,
    const al_browser_product_v1 *product)
{
    al_browser_span_v1 name;
    name.start = product->name_start;
    name.length = product->name_length;
    return al_span_contains_ascii_v1(input, name, "bot", 0);
}

static int al_is_bot_v1(
    const uint16_t *input,
    const al_browser_product_table_v1 *table,
    al_browser_kind_v1 kind)
{
    int application = al_application_index_v1(table, kind);
    uint32_t product_index;
    if (application < 0) {
        return 1;
    }
    for (product_index = 0u; product_index < table->count; ++product_index) {
        const al_browser_product_v1 *product = &table->products[product_index];
        uint32_t comment_index;
        for (comment_index = 0u; comment_index < product->comment_count; ++comment_index) {
            al_browser_span_v1 comment;
            if (al_comment_segment_v1(input, product, comment_index, &comment)
                && al_span_contains_ascii_v1(input, comment, "bot", 1)) {
                return 1;
            }
        }
    }
    return al_has_product_v1(input, table, "Chrome-Lighthouse")
        || al_application_name_contains_bot_v1(input, &table->products[(uint32_t)application]);
}

static uint32_t al_version_output_length_v1(al_browser_version_v1 version)
{
    return version.length + version.ascii_length
        + (version.prefix_ios != 0u ? 4u : 0u);
}

static void al_copy_version_v1(
    al_browser_version_v1 version,
    uint16_t *output)
{
    uint32_t offset = 0u;
    uint32_t index;
    if (version.prefix_ios != 0u) {
        static const uint16_t prefix[] = { 'i', 'O', 'S', ' ' };
        for (index = 0u; index < 4u; ++index) {
            output[offset++] = prefix[index];
        }
    }
    if (version.ascii != NULL) {
        for (index = 0u; index < version.ascii_length; ++index) {
            output[offset++] = (uint16_t)(unsigned char)version.ascii[index];
        }
    }
    if (version.units != NULL) {
        for (index = 0u; index < version.length; ++index) {
            uint16_t character = version.units[version.start + index];
            if (version.replace_underscores != 0u && character == (uint16_t)'_') {
                character = (uint16_t)'.';
            }
            output[offset++] = character;
        }
    }
}

size_t al_campfire_browser_scratch_size_v1(void)
{
    return sizeof(al_browser_scratch_v1);
}

size_t al_campfire_browser_scratch_alignment_v1(void)
{
    return _Alignof(al_browser_scratch_v1);
}

al_campfire_browser_status_v1 al_campfire_browser_normalize_utf16_v1(
    uint32_t present,
    const uint16_t *input,
    size_t input_units,
    void *scratch,
    size_t scratch_bytes,
    uint16_t *version_output,
    size_t version_capacity_units,
    al_campfire_browser_result_v1 *result)
{
    al_browser_scratch_v1 *scratch_state;
    al_browser_product_table_v1 *table;
    const uint16_t *effective_input;
    al_campfire_browser_status_v1 status;
    al_browser_kind_v1 kind;
    al_browser_version_v1 version;
    al_campfire_browser_result_v1 local_result;
    uint32_t version_length;
    size_t alignment;
    uintptr_t scratch_address;

    if (result == NULL || present > 1u || input_units > (size_t)AL_CAMPFIRE_BROWSER_MAX_INPUT_UNITS_V1
        || (input_units > 0u && input == NULL)
        || (present == 0u && (input != NULL || input_units != 0u))
        || (version_capacity_units > 0u && version_output == NULL)) {
        return input_units > (size_t)AL_CAMPFIRE_BROWSER_MAX_INPUT_UNITS_V1
            ? AL_CAMPFIRE_BROWSER_INPUT_LIMIT_V1
            : AL_CAMPFIRE_BROWSER_INVALID_ARGUMENT_V1;
    }
    if (scratch == NULL || scratch_bytes < sizeof(al_browser_scratch_v1)) {
        return AL_CAMPFIRE_BROWSER_SCRATCH_EXTENT_V1;
    }
    alignment = _Alignof(al_browser_scratch_v1);
    scratch_address = (uintptr_t)scratch;
    if (scratch_address % alignment != 0u) {
        return AL_CAMPFIRE_BROWSER_SCRATCH_ALIGNMENT_V1;
    }

    scratch_state = (al_browser_scratch_v1 *)scratch;
    table = &scratch_state->products;
    status = al_parse_products_v1(input, (uint32_t)input_units, present, table, &effective_input);
    if (status != AL_CAMPFIRE_BROWSER_OK_V1) {
        return status;
    }
    kind = al_browser_kind_v1_for_products(effective_input, table);
    version = al_compute_version_v1(effective_input, table, kind, scratch_state->joined_text);
    local_result.family = (uint32_t)al_browser_family_v1(effective_input, table, kind);
    local_result.has_version = al_version_is_blank_v1(version) ? 0u : 1u;
    local_result.version_units = 0u;
    local_result.is_bot = al_is_bot_v1(effective_input, table, kind) ? 1u : 0u;

    if (local_result.has_version != 0u) {
        version_length = al_version_output_length_v1(version);
        if ((size_t)version_length > version_capacity_units) {
            return AL_CAMPFIRE_BROWSER_VERSION_EXTENT_V1;
        }
        if (version_length > 0u && version_output == NULL) {
            return AL_CAMPFIRE_BROWSER_INVALID_ARGUMENT_V1;
        }
        al_copy_version_v1(version, version_output);
        local_result.version_units = version_length;
    }
    *result = local_result;
    return AL_CAMPFIRE_BROWSER_OK_V1;
}
