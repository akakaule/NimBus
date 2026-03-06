// Use this file to customize razzle's webpack.config.js

module.exports = {
  plugins: ["typescript"],

  modify: (config, { target, dev }, webpack) => {
    // configure https for the dev server
    // trust my devcert.pfx, or generate your own:

    // openssl req -x509 -newkey rsa:4096 -keyout key.pem -out cert.pem -days 10000 -nodes
    // openssl pkcs12 -export -out devcert.pfx -inkey key.pem -in cert.pem

    // On first run, you must visit https://localhost:3001 and trust the certificate
    if (dev) {
      config.output.publicPath = config.output.publicPath.replace(
        "http://",
        "https://"
      );
      if (target === "web") {
        config.devServer.https = true;
        config.devServer.pfx = process.env.RAZZLE_PFX;
        config.devServer.pfxPassphrase = "";
        config.devServer.proxy = {
          "/api/*": {
            target: "https://localhost:55076",
            secure: false,
          },
        };
      }
    }

    return config;
  },
};
