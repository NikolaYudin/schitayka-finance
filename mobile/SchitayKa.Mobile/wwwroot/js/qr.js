// Мост для декодирования QR-кода чека ФНС внутри BlazorWebView.
// jsQR работает локально на устройстве — изображение никуда не отправляется.
window.skDecodeQr = async function (dataUrl) {
  const img = new Image();
  img.src = dataUrl;
  await img.decode();
  const canvas = document.createElement('canvas');
  canvas.width = img.width;
  canvas.height = img.height;
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  ctx.drawImage(img, 0, 0);
  const data = ctx.getImageData(0, 0, canvas.width, canvas.height);
  const code = window.jsQR(data.data, data.width, data.height, { inversionAttempts: 'dontInvert' })
    || window.jsQR(data.data, data.width, data.height, { inversionAttempts: 'attemptInvert' });
  return code ? code.data : null;
};
