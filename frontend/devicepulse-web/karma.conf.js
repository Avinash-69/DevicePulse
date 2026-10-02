// Karma configuration.
//
// Only reason this file exists rather than relying on the CLI defaults: a custom launcher that
// passes --no-sandbox, which headless Chrome needs when it runs in CI containers. Without it the
// test job fails on the build agent while passing locally, which is the worst kind of difference.
module.exports = function (config) {
  config.set({
    basePath: '',
    frameworks: ['jasmine', '@angular-devkit/build-angular'],
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter'),
      require('karma-coverage'),
      require('@angular-devkit/build-angular/plugins/karma'),
    ],
    client: {
      jasmine: {
        // Random order by default, so a test that depends on another one's leftovers fails
        // loudly instead of passing by luck.
        random: true,
      },
      clearContext: false,
    },
    reporters: ['progress', 'kjhtml'],
    browsers: ['ChromeHeadlessNoSandbox'],
    customLaunchers: {
      ChromeHeadlessNoSandbox: {
        base: 'ChromeHeadless',
        flags: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'],
      },
    },
    restartOnFileChange: true,
  });
};
