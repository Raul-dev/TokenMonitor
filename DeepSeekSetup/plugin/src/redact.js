/** Превью/длины текста для приватности (PLAN §5.8, §5.2 captureText). */

/**
 * @param {string} text
 * @param {'off'|'preview'|'full'} mode
 * @param {number} limit
 * @returns {{ preview: string|null, chars: number }}
 */
export function textPreview(text, mode, limit = 200) {
  const value = typeof text === 'string' ? text : '';
  const chars = value.length;
  if (mode === 'off' || chars === 0) return { preview: null, chars };
  if (mode === 'full') return { preview: value, chars };
  return {
    preview: chars > limit ? value.slice(0, limit) + '…' : value,
    chars,
  };
}

/** Склеивает текстовые блоки сообщения; не-текстовые блоки игнорирует. */
export function textOfContent(content) {
  if (!Array.isArray(content)) return '';
  const parts = [];
  for (const block of content) {
    if (!block || typeof block !== 'object') continue;
    if (block.type === 'text' && typeof block.text === 'string') parts.push(block.text);
  }
  return parts.join('\n');
}
