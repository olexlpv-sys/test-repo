import {
  createTheme,
  defaultVariantColorsResolver,
  type MantineColorsTuple,
  type VariantColorsResolver,
} from '@mantine/core';

// Primary blue #1E5BDC at shade 6 (buttons, active tab, links).
const brand: MantineColorsTuple = [
  '#eaf1fb',
  '#d6e3fa',
  '#adc6f4',
  '#84a9ee',
  '#5b8ce8',
  '#3270e2',
  '#1e5bdc',
  '#1a4fc0',
  '#1643a3',
  '#113786',
];

// `variant="soft"`: tinted background, coloured text (secondary actions: "Open", "Delete" in modal footers).
const variantColorResolver: VariantColorsResolver = (input) => {
  if (input.variant === 'soft') {
    switch (input.color) {
      case 'red':
        return { background: '#fdecec', hover: '#fbdcdc', color: '#b42318', border: 'none' };
      case 'gray':
        return { background: '#f2f4f7', hover: '#e7eaef', color: '#3d4655', border: 'none' };
      default:
        return { background: '#eaf1fb', hover: '#dce8fa', color: '#1b4fc4', border: 'none' };
    }
  }

  return defaultVariantColorsResolver(input);
};

export const theme = createTheme({
  variantColorResolver,
  primaryColor: 'brand',
  primaryShade: 6,
  colors: { brand },
  fontFamily: 'Inter, -apple-system, "Segoe UI", Roboto, sans-serif',
  headings: { fontFamily: 'Inter, -apple-system, "Segoe UI", Roboto, sans-serif', fontWeight: '500' },
  defaultRadius: 'md',
  radius: { md: '8px', lg: '10px' },
  black: '#2b3441',
  components: {
    Button: {
      defaultProps: { radius: 'md' },
      styles: { root: { fontWeight: 500 } },
    },
    Input: {
      styles: {
        input: {
          minHeight: 38,
          height: 38,
          borderColor: 'var(--dh-input-border)',
          color: 'var(--dh-text)',
          fontSize: 15,
        },
      },
    },
    InputWrapper: {
      styles: { label: { fontSize: 13, color: 'var(--dh-tab)', marginBottom: 8, fontWeight: 400 } },
    },
    Modal: {
      defaultProps: { radius: 12, padding: 20, size: 540, centered: true },
      styles: {
        header: { borderBottom: '1px solid var(--dh-border)', minHeight: 68, marginBottom: 20 },
        title: { fontSize: 20, fontWeight: 500, color: 'var(--dh-text)' },
        close: { width: 38, height: 38, background: '#eef1f4', borderRadius: 8, color: 'var(--dh-tab)' },
      },
    },
    Tooltip: { defaultProps: { radius: 'md' } },
    Menu: { defaultProps: { radius: 'md', shadow: 'md' } },
  },
});
